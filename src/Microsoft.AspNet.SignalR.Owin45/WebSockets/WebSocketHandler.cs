﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.md in the project root for license information.

using System;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR.Infrastructure;

namespace Microsoft.AspNet.SignalR.WebSockets
{
    public class WebSocketHandler
    {
        // Wait 250 ms before giving up on a Close
        private static readonly TimeSpan _closeTimeout = TimeSpan.FromMilliseconds(250);

        // 4K default fragment size (we expect most messages to be very short)
        private const int _receiveLoopBufferSize = 4 * 1024;

        // 4MB default max incoming message size
        private int _maxIncomingMessageSize = 4 * 1024 * 1024;

        // Queue for sending messages
        private readonly TaskQueue _sendQueue = new TaskQueue();

        private volatile bool _isClosed;

        public virtual void OnOpen() { }

        public virtual void OnMessage(string message) { throw new NotImplementedException(); }

        public virtual void OnMessage(byte[] message) { throw new NotImplementedException(); }
        
        public virtual void OnError() { }

        public virtual void OnClose(bool clean) { }

        // Sends a text message to the client
        public Task Send(string message)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            return SendAsync(message);
        }

        internal Task SendAsync(string message)
        {
            return SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text);
        }

        internal Task SendAsync(byte[] message, WebSocketMessageType messageType)
        {
            if (_isClosed)
            {
                return TaskAsyncHelper.Empty;
            }

            var sendContext = new SendContext(this, message, messageType);

            return _sendQueue.Enqueue(state =>
            {
                var context = (SendContext)state;

                if (context.Handler._isClosed)
                {
                    return TaskAsyncHelper.Empty;
                }

                return context.Handler.WebSocket.SendAsync(new ArraySegment<byte>(context.Message), context.MessageType, true /* endOfMessage */, CancellationToken.None);
            },
            sendContext);
        }

        // Gracefully closes the connection
        public virtual void Close()
        {
            CloseAsync();
        }

        internal Task CloseAsync()
        {
            if (_isClosed)
            {
                return TaskAsyncHelper.Empty;
            }

            var closeContext = new CloseContext(this);

            return _sendQueue.Enqueue(state =>
            {
                var context = (CloseContext)state;

                if (context.Handler._isClosed)
                {
                    return TaskAsyncHelper.Empty;
                }

                context.Handler._isClosed = true;
                return context.Handler.WebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            },
            closeContext);
        }

        public int MaxIncomingMessageSize
        {
            get
            {
                return _maxIncomingMessageSize;
            }
        }
 
        public WebSocket WebSocket { get; set; }

        public Exception Error { get; set; }

        public Task ProcessWebSocketRequestAsync(WebSocket webSocket, CancellationToken disconnectToken)
        {
            if (webSocket == null)
            {
                throw new ArgumentNullException("webSocket");
            }

            var receiveContext = new ReceiveContext(webSocket, disconnectToken, MaxIncomingMessageSize, _receiveLoopBufferSize);

            return ProcessWebSocketRequestAsync(webSocket, disconnectToken, state =>
            {
                var context = (ReceiveContext)state;

                return WebSocketMessageReader.ReadMessageAsync(context.WebSocket, context.BufferSize, context.MaxIncomingMessageSize, context.DisconnectToken);
            },
            receiveContext);
        }

        internal async Task ProcessWebSocketRequestAsync(WebSocket webSocket, CancellationToken disconnectToken, Func<object, Task<WebSocketMessage>> messageRetriever, object state)
        {
            bool cleanClose = true;
            try
            {
                _isClosed = false;

                // first, set primitives and initialize the object
                WebSocket = webSocket;
                OnOpen();

                // dispatch incoming messages
                while (!disconnectToken.IsCancellationRequested)
                {
                    WebSocketMessage incomingMessage = await messageRetriever(state);
                    switch (incomingMessage.MessageType)
                    {
                        case WebSocketMessageType.Binary:
                            OnMessage((byte[])incomingMessage.Data);
                            break;

                        case WebSocketMessageType.Text:
                            OnMessage((string)incomingMessage.Data);
                            break;

                        default:
                            // If we received an incoming CLOSE message, we'll queue a CLOSE frame to be sent.
                            // We'll give the queued frame some amount of time to go out on the wire, and if a
                            // timeout occurs we'll give up and abort the connection.
                            await Task.WhenAny(CloseAsync(), Task.Delay(_closeTimeout))
                                .ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously); // swallow exceptions occurring from sending the CLOSE
                            return;
                    }
                }

            }
            catch (OperationCanceledException ex)
            {
                if (!ex.CancellationToken.IsCancellationRequested)
                {
                    Error = ex;
                    OnError();
                    cleanClose = false;
                }
            }
            catch (Exception ex)
            {
                if (IsFatalException(ex))
                {
                    Error = ex;
                    OnError();
                    cleanClose = false;
                }
            }
            finally
            {
                try
                {
                    try
                    {
                        Close();
                    }
                    finally
                    {
                        OnClose(cleanClose);
                    }
                }
                finally
                {
                    // call Dispose if it exists
                    IDisposable disposable = this as IDisposable;
                    if (disposable != null)
                    {
                        disposable.Dispose();
                    }
                }
            }
        }

        // returns true if this is a fatal exception (e.g. OnError should be called)
        private static bool IsFatalException(Exception ex)
        {
            // If this exception is due to the underlying TCP connection going away, treat as a normal close
            // rather than a fatal exception.
            COMException ce = ex as COMException;
            if (ce != null)
            {
                switch ((uint)ce.ErrorCode)
                {
                    // These are the three error codes we've seen in testing which can be caused by the TCP connection going away unexpectedly.
                    case 0x800703e3:
                    case 0x800704cd:
                    case 0x80070026:
                        return false;
                }
            }

            // unknown exception; treat as fatal
            return true;
        }

        private class CloseContext
        {
            public WebSocketHandler Handler;

            public CloseContext(WebSocketHandler webSocketHandler)
            {
                Handler = webSocketHandler;
            }
        }

        private class SendContext
        {
            public WebSocketHandler Handler;
            public byte[] Message;
            public WebSocketMessageType MessageType;

            public SendContext(WebSocketHandler webSocketHandler, byte[] message, WebSocketMessageType messageType)
            {
                Handler = webSocketHandler;
                Message = message;
                MessageType = messageType;
            }
        }

        private class ReceiveContext
        {
            public WebSocket WebSocket;
            public CancellationToken DisconnectToken;
            public int MaxIncomingMessageSize;
            public int BufferSize;

            public ReceiveContext(WebSocket webSocket, CancellationToken disconnectToken, int maxIncomingMessageSize, int bufferSize)
            {
                WebSocket = webSocket;
                DisconnectToken = disconnectToken;
                MaxIncomingMessageSize = maxIncomingMessageSize;
                BufferSize = bufferSize;
            }
        }
    }
}