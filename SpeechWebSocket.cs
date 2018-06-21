/**
* Copyright 2018 IBM Corp. All Rights Reserved.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
*      http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*
*/

using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Collections.Generic;
using System.Diagnostics;

namespace WatsonCore.Services
{
    public class SpeechWebSocketEventCloseArgs : EventArgs
    {
        public SpeechWebSocketEventCloseArgs(int code, string reason) { Code = code; Reason = reason; }
        public int Code { get; private set; }
        public string Reason { get; private set; }
    }

    public class SpeechWebSocketEventMessageArgs : EventArgs
    {
        public SpeechWebSocketDataType Type { get; private set; }
        public byte[] Data { get; private set; }
        public string Text { get; private set; }
        public int Length { get { return Data.Length; } }

        public SpeechWebSocketEventMessageArgs(byte[] data, string text, SpeechWebSocketDataType type)
        {
            Data = data;
            Text = text;
            Type = type;
        }
    }

    public class SpeechWebSocketEventErrorArgs : EventArgs
    {
        public SpeechWebSocketEventErrorArgs(Exception exception) { Exception = exception ?? new Exception("Unknown Exception"); }
        public Exception Exception { get; private set; }
    }

    public class SpeechWebSocketEventOpenArgs : EventArgs { }

    public enum SpeechWebSocketDataType
    {
        Text,
        Binary
    }

    public enum SpeechWebSocketState
    {
        Closed,
        Closing,
        Open,
        Connecting
    }

    public class SpeechWebSocket
    {
        private const int SendChunkSize = 1024;
        private const int ReceiveChunkSize = 4096;

        protected ClientWebSocket ClientWebSocket { get; set; }
        protected UriBuilder UriBuilder { get; set; }
        protected Dictionary<string, string> QueryString { get; set; }
        protected Dictionary<string, string> RequestHeaders { get; set; }
        protected CancellationTokenSource SendingTokenSource { get; set; }
        protected CancellationTokenSource ReceiveTokenSource { get; set; }

        // Only for the compatibility with WebSocket-Sharp
        public SpeechWebSocketState ReadyState
        {
            get
            {
                return State;
            }
        }

        public SpeechWebSocketState State
        {
            get
            {
                switch (ClientWebSocket.State)
                {
                    case WebSocketState.Closed:
                    case WebSocketState.Aborted:
                    case WebSocketState.None:
                        return SpeechWebSocketState.Closed;
                    case WebSocketState.Open:
                        return SpeechWebSocketState.Open;
                    case WebSocketState.Connecting:
                        return SpeechWebSocketState.Connecting;
                    case WebSocketState.CloseSent:
                        return SpeechWebSocketState.Closing;
                    case WebSocketState.CloseReceived:
                        return SpeechWebSocketState.Closed;
                    default:
                        return SpeechWebSocketState.Closed;
                }
            }
        }

        public Action<SpeechWebSocketEventOpenArgs> OnOpen = (evt) => { };
        public Action<SpeechWebSocketEventMessageArgs> OnMessage = (evt) => { };
        public Action<SpeechWebSocketEventErrorArgs> OnError = (ext) => { };
        public Action<SpeechWebSocketEventCloseArgs> OnClose = (evt) => { };

        public SpeechWebSocket(string urlService)
        {
            RequestHeaders = new Dictionary<string, string>();
            ClientWebSocket = new ClientWebSocket();
            UriBuilder = new UriBuilder(urlService);
            QueryString = new Dictionary<string, string>();
        }

        private void InitializeCancellationTokenSource()
        {
            SendingTokenSource = new CancellationTokenSource();
            ReceiveTokenSource = new CancellationTokenSource();
        }

        public SpeechWebSocket AddArgument(string argumentName, string argumentValue)
        {
            if (QueryString.ContainsKey(argumentName))
                QueryString[argumentName] = argumentValue;
            else
                QueryString.Add(argumentName, argumentValue);

            UriBuilder.Query =
                string.Join("&", QueryString.Keys.Where(key => !string.IsNullOrWhiteSpace(QueryString[key])).Select(key => string.Format("{0}={1}", WebUtility.UrlEncode(key), WebUtility.UrlEncode(QueryString[key]))));

            return this;
        }

        public SpeechWebSocket SetCredentials(string userName, string password, bool isSSL = false)
        {
            RequestHeaders.Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(userName + ":" + password)));
            if (isSSL)
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls | SecurityProtocolType.Ssl3;
            return this;
        }

        public SpeechWebSocket WithHeader(string headerName, string headerValue)
        {
            RequestHeaders.Add(headerName, headerValue);
            return this;
        }

        protected async Task OnReceiving()
        {
            while (true)
            {
                var bufferBytes = new byte[ReceiveChunkSize];
                var bufferSegment = new ArraySegment<byte>(bufferBytes);
                WebSocketReceiveResult result = null;
                ReceiveTokenSource.Token.ThrowIfCancellationRequested();
                do
                {
                    ReceiveTokenSource.Token.ThrowIfCancellationRequested();
                    if (ClientWebSocket.State == WebSocketState.Open || ClientWebSocket.State == WebSocketState.CloseSent)
                    {
                        result = await ClientWebSocket.ReceiveAsync(bufferSegment, CancellationToken.None);
                        string text = string.Empty;
                        var buffer = bufferBytes.Skip(bufferSegment.Offset).Take(result.Count).ToArray();

                        switch (result.MessageType)
                        {
                            case WebSocketMessageType.Close:
                                var status = result.CloseStatus ?? ClientWebSocket.CloseStatus ?? WebSocketCloseStatus.Empty;
                                var description = result.CloseStatusDescription;
                                SpeechWebSocketLogger.Log("### SpeechWebSocket.CLOSING: {0} -> {1} ###", status, description);
                                await CloseAsync(status, description);
                                SpeechWebSocketLogger.Log("### SpeechWebSocket.CLOSED ###");
                                OnClose(new SpeechWebSocketEventCloseArgs((int)status, description));
                                return;
                            case WebSocketMessageType.Binary:
                                // text = Encoding.UTF8.GetString(buffer, 0, buffer.Length);
                                OnMessage(new SpeechWebSocketEventMessageArgs(buffer, text, SpeechWebSocketDataType.Binary));
                                break;
                            case WebSocketMessageType.Text:
                                SpeechWebSocketLogger.Log("### SpeechWebSocket.TEXT ###");
                                text = Encoding.UTF8.GetString(buffer, 0, buffer.Length);
                                OnMessage(new SpeechWebSocketEventMessageArgs(buffer, text, SpeechWebSocketDataType.Text));
                                break;
                        }
                    }
                    //else
                    //{
                    //    OnClose(new SpeechWebSocketEventCloseArgs(1000));
                    //    return;
                    //}

                } while (result.EndOfMessage == false);
            }
        }

        protected async Task Send(byte[] data, WebSocketMessageType type)
        {
            var messagesCount = (int)Math.Ceiling((double)data.Length / SendChunkSize);

            for (var i = 0; i < messagesCount; i++)
            {
                var offset = (SendChunkSize * i);
                var count = SendChunkSize;
                var lastMessage = ((i + 1) == messagesCount);

                if ((count * (i + 1)) > data.Length)
                {
                    count = data.Length - offset;
                }

                SendingTokenSource.Token.ThrowIfCancellationRequested();

                if (ClientWebSocket.State == WebSocketState.Open)
                {
                    await ClientWebSocket.SendAsync(new ArraySegment<byte>(data, offset, count), type, lastMessage, CancellationToken.None);
                }
            }
        }

        public async Task SendAsync(byte[] data)
        {
            await Send(data, WebSocketMessageType.Binary);
        }

        public async Task SendAsync(string message)
        {
            var messageBuffer = Encoding.UTF8.GetBytes(message);
            await Send(messageBuffer, WebSocketMessageType.Text);
        }

        public void Send(string message)
        {
            Task.Factory.StartNew(() => SendAsync(message), SendingTokenSource.Token);
        }

        public void Send(byte[] data)
        {
            Task.Factory.StartNew(() => SendAsync(data), SendingTokenSource.Token);
        }

        public void Connect()
        {
            InitializeCancellationTokenSource();
            Task.Factory.StartNew(() =>
            {
                try
                {
                    var dateTime = DateTime.Now;
                    SpeechWebSocketLogger.Log("### SpeechWebSocket.CONNECT[{0}]: {1} ###", ClientWebSocket.State, UriBuilder.Uri);

                    if (ClientWebSocket.State == WebSocketState.None || ClientWebSocket.State == WebSocketState.Closed)
                    {
                        ClientWebSocket = new ClientWebSocket();
                        foreach (var item in RequestHeaders)
                        {
                            ClientWebSocket.Options.SetRequestHeader(item.Key, item.Value);
                        }

                        SendingTokenSource.Token.ThrowIfCancellationRequested();
                        var task = ClientWebSocket.ConnectAsync(UriBuilder.Uri, CancellationToken.None);
                        task.Wait();
                        SpeechWebSocketLogger.Log("### SpeechWebSocket.CONNECT: {0}ms ###", (DateTime.Now - dateTime).Milliseconds);

                        switch (task.Status)
                        {
                            case TaskStatus.RanToCompletion:
                                var receive = OnReceiving();
                                OnOpen(new SpeechWebSocketEventOpenArgs());
                                try
                                {
                                    receive.Wait();
                                }
                                catch (AggregateException ex)
                                {
                                    SpeechWebSocketLogger.Log("### SpeechWebSocket.RECEIVING EXCEPTIONS: {0} ###", ex.Message);
                                    foreach (var v in ex.InnerExceptions)
                                        SpeechWebSocketLogger.Log("### SpeechWebSocket.RECEIVING MESSAGE: {0} ###", v.Message);
                                }
                                catch (ObjectDisposedException ex)
                                {
                                    SpeechWebSocketLogger.Log("### SpeechWebSocket.RECEIVING EXCEPTION: {0} ###", ex.Message);
                                }

                                break;
                            default:
                                OnError(new SpeechWebSocketEventErrorArgs(task.Exception));
                                break;
                        }
                        return;
                    }
                    var closeTask = CloseAsync(WebSocketCloseStatus.Empty);
                    closeTask.Wait();
                    Connect();
                }
                catch (Exception ex)
                {
                    SpeechWebSocketLogger.Log("### SpeechWebSocket.EXCEPTION ###");

                    OnError(new SpeechWebSocketEventErrorArgs(ex));
                }
            });
        }

        public Task CloseAsync(WebSocketCloseStatus webSocketCloseStatus = WebSocketCloseStatus.NormalClosure, string reason = "Passive Closure")
        {
            SpeechWebSocketLogger.Log("### SpeechWebSocket.STATUS: {0} ###", ClientWebSocket.State);

            switch (ClientWebSocket.State)
            {
                case WebSocketState.Closed:
                case WebSocketState.None:
                    ReceiveTokenSource?.Cancel();
                    return Task.CompletedTask;
                case WebSocketState.Aborted:
                case WebSocketState.CloseReceived:
                    SendingTokenSource?.Cancel();
                    return Task.CompletedTask;
                case WebSocketState.CloseSent:
                    SendingTokenSource?.Cancel();
                    return ClientWebSocket.CloseAsync(webSocketCloseStatus, reason, CancellationToken.None);
                case WebSocketState.Connecting:
                    ReceiveTokenSource?.Cancel();
                    ClientWebSocket.Abort();
                    return Task.CompletedTask;
                case WebSocketState.Open:
                default:
                    SendingTokenSource?.Cancel();
                    return ClientWebSocket.CloseOutputAsync(webSocketCloseStatus, reason, CancellationToken.None);
            }
        }
    }
    
    internal class SpeechWebSocketLogger
    {
        internal static StringBuilder LogBuilder = new StringBuilder();
        public static EventHandler<string> OnDebugging = null;

        public static void Log(string formattedString, params object[] args)
        {
#if DEBUG
            Debug.WriteLine("[{0}] ---> {1}", DateTime.Now.ToString("HH:mm:ss fff"), string.Format(formattedString, args));
#else
            LogBuilder.AppendFormat("[{0}] ---> {1}", DateTime.Now.ToString("HH:mm:ss fff"), string.Format(formattedString, args));
            LogBuilder.AppendLine();
            OnDebugging?.Invoke(null, string.Empty);
#endif
        }
        public static void Log(string formattedString)
        {
#if DEBUG
            Debug.WriteLine("[{0}] ---> {1}", DateTime.Now.ToString("HH:mm:ss fff"), formattedString);
#else
            LogBuilder.AppendFormat("[{0}] ---> {1}", DateTime.Now.ToString("HH:mm:ss fff"), formattedString);
            LogBuilder.AppendLine();
            OnDebugging?.Invoke(null, string.Empty);
#endif
        }
        public static void LogLine()
        {
            Log("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++");
        }
        public static void LogWrite(string str)
        {
#if DEBUG
            Debug.Write(str);
#else
            LogBuilder.Append(str);
            OnDebugging?.Invoke(null, string.Empty);
#endif
        }
        public static void Log(object obj)
        {
#if DEBUG
            Debug.WriteLine(obj);
#else
            LogBuilder.Append(obj.ToString());
            LogBuilder.AppendLine();
            OnDebugging?.Invoke(null, string.Empty);
#endif
        }

        public static void Flush()
        {
            Console.Write(LogBuilder.ToString());
        }
    }
}
