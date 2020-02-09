using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using OBSWebsocket.NETStandard.Types;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using WebSocketSharp;

namespace OBSWebsocket.NETStandard
{
    public partial class OBSWebsocket
    {
        private delegate void RequestCallback(OBSWebsocket sender, JObject body);
        private readonly Dictionary<string, TaskCompletionSource<JObject>> _responseHandlers;

        private TimeSpan _pWSTimeout;

        /// <summary>
        /// WebSocket request timeout, represented as a TimeSpan object
        /// </summary>
        public TimeSpan WSTimeout
        {
            get
            {
                if (WSConnection != null)
                    return WSConnection.WaitTime;
                else
                    return _pWSTimeout;
            }
            set
            {
                _pWSTimeout = value;

                if (WSConnection != null)
                {
                    WSConnection.WaitTime = _pWSTimeout;
                }
            }
        }

        /// <summary>
        /// Underlying WebSocket connection to an obs-websocket server. Value is null when disconnected.
        /// </summary>
        public WebSocket WSConnection { get; private set; }


        public OBSWebsocket()
        {
            _responseHandlers = new Dictionary<string, TaskCompletionSource<JObject>>();
        }

        /// <summary>
        /// Connect this instance to the specified URL, and authenticate (if needed) with the specified password
        /// </summary>
        /// <param name="url">Server URL in standard URL format</param>
        /// <param name="password">Server password</param>
        public void Connect(string url, string password)
        {
            if (WSConnection != null && WSConnection.IsAlive)
            {
                Disconnect();
            }

            WSConnection = new WebSocket(url)
            {
                WaitTime = _pWSTimeout
            };
            WSConnection.OnMessage += WebsocketMessageHandler;
            WSConnection.OnClose += (s, e) =>
            {
                Disconnected?.Invoke(this, e);
            };
            WSConnection.Connect();

            if (!WSConnection.IsAlive)
            {
                return;
            }

            OBSAuthInfo authInfo = GetAuthInfo();

            if (authInfo.AuthRequired)
            {
                Authenticate(password, authInfo);
            }

            Connected?.Invoke(this, null);
        }

        /// <summary>
        /// Disconnect this instance from the server
        /// </summary>
        public void Disconnect()
        {
            if (WSConnection != null)
            {
                WSConnection.Close();
            }

            WSConnection = null;

            foreach (var cb in _responseHandlers)
            {
                var tcs = cb.Value;
                tcs.TrySetCanceled();
            }
        }

        // This callback handles incoming JSON messages and determines if it's
        // a request response or an event ("Update" in obs-websocket terminology)
        private void WebsocketMessageHandler(object sender, MessageEventArgs e)
        {
            if (!e.IsText)
                return;

            JObject body = JObject.Parse(e.Data);

            if (body["message-id"] != null)
            {
                // Handle a request :
                // Find the response handler based on
                // its associated message ID
                string msgID = (string)body["message-id"];
                var handler = _responseHandlers[msgID];

                if (handler != null)
                {
                    // Set the response body as Result and notify the request sender
                    handler.SetResult(body);

                    // The message with the given ID has been processed,
                    // so its handler can be discarded
                    _responseHandlers.Remove(msgID);
                }
            }
            else if (body["update-type"] != null)
            {
                // Handle an event
                string eventType = body["update-type"].ToString();
                ProcessEventType(eventType, body);
            }
        }

        public void FireRequest(string requestType, JObject additionalFields = null)
        {
            SendRequest(requestType, additionalFields);
        }

        public JObject SendRequest(string requestType, JObject additionalFields = null)
        {
            string messageID;

            // Generate a random message id and make sure it is unique within the handlers dictionary
            do
            {
                messageID = NewMessageID();
            }
            while (_responseHandlers.ContainsKey(messageID));

            // Build the bare-minimum body for a request
            var body = new JObject
            {
                { "request-type", requestType },
                { "message-id", messageID }
            };

            // Add optional fields if provided
            if (additionalFields != null)
            {
                body.Merge(additionalFields, new JsonMergeSettings()
                {
                    MergeArrayHandling = MergeArrayHandling.Union
                });
            }

            // Prepare the asynchronous response handler
            var tcs = new TaskCompletionSource<JObject>();
            _responseHandlers.Add(messageID, tcs);


            // Send the message and wait for a response
            // (received and notified by the websocket response handler)
            WSConnection.Send(body.ToString());
            tcs.Task.Wait();

            if (tcs.Task.IsCanceled)
            {
                throw new ErrorResponseException("Request canceled");
            }

            // Throw an exception if the server returned an error.
            // An error occurs if authentication fails or one if the request body is invalid.
            var result = tcs.Task.Result;

            if ((string)result["status"] == "error")
            {
                throw new ErrorResponseException((string)result["error"]);
            }

            return result;
        }

        /// <summary>
        /// Authenticates to the Websocket server using the challenge and salt given in the passed <see cref="OBSAuthInfo"/> object
        /// </summary>
        /// <param name="password">User password</param>
        /// <param name="authInfo">Authentication data</param>
        /// <returns>true if authentication succeeds, false otherwise</returns>
        public bool Authenticate(string password, OBSAuthInfo authInfo)
        {
            string secret = HashEncode(password + authInfo.PasswordSalt);
            string authResponse = HashEncode(secret + authInfo.Challenge);

            var requestFields = new JObject
            {
                { "auth", authResponse }
            };

            try
            {
                // Throws ErrorResponseException if auth fails
                FireRequest("Authenticate", requestFields);
            }
            catch (ErrorResponseException)
            {
                throw new AuthFailureException();
            }

            return true;
        }


        /// <summary>
        /// Update message handler
        /// </summary>
        /// <param name="eventType">Value of "event-type" in the JSON body</param>
        /// <param name="body">full JSON message body</param>
        protected void ProcessEventType(string eventType, JObject body)
        {
            StreamStatus status;

            switch (eventType)
            {
                case "SwitchScenes":
                    SceneChanged?.Invoke(this, (string)body["scene-name"]);
                    break;

                case "ScenesChanged":
                    SceneListChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case "SourceOrderChanged":
                    SourceOrderChanged?.Invoke(this, (string)body["scene-name"]);
                    break;

                case "SceneItemAdded":
                    SceneItemAdded?.Invoke(this, (string)body["scene-name"], (string)body["item-name"]);
                    break;

                case "SceneItemRemoved":
                    SceneItemRemoved?.Invoke(this, (string)body["scene-name"], (string)body["item-name"]);
                    break;

                case "SceneItemVisibilityChanged":
                    SceneItemVisibilityChanged?.Invoke(this, (string)body["scene-name"], (string)body["item-name"]);
                    break;

                case "SceneCollectionChanged":
                    SceneCollectionChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case "SceneCollectionListChanged":
                    SceneCollectionListChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case "SwitchTransition":
                    TransitionChanged?.Invoke(this, (string)body["transition-name"]);
                    break;

                case "TransitionDurationChanged":
                    TransitionDurationChanged?.Invoke(this, (int)body["new-duration"]);
                    break;

                case "TransitionListChanged":
                    TransitionListChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case "TransitionBegin":
                    TransitionBegin?.Invoke(this, EventArgs.Empty);
                    break;

                case "ProfileChanged":
                    ProfileChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case "ProfileListChanged":
                    ProfileListChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case "StreamStarting":
                    StreamingStateChanged?.Invoke(this, OutputState.Starting);
                    break;

                case "StreamStarted":
                    StreamingStateChanged?.Invoke(this, OutputState.Started);
                    break;

                case "StreamStopping":
                    StreamingStateChanged?.Invoke(this, OutputState.Stopping);
                    break;

                case "StreamStopped":
                    StreamingStateChanged?.Invoke(this, OutputState.Stopped);
                    break;

                case "RecordingStarting":
                    RecordingStateChanged?.Invoke(this, OutputState.Starting);
                    break;

                case "RecordingStarted":
                    RecordingStateChanged?.Invoke(this, OutputState.Started);
                    break;

                case "RecordingStopping":
                    RecordingStateChanged?.Invoke(this, OutputState.Stopping);
                    break;

                case "RecordingStopped":
                    RecordingStateChanged?.Invoke(this, OutputState.Stopped);
                    break;

                case "StreamStatus":
                    if (StreamStatus != null)
                    {
                        status = new StreamStatus(body);
                        StreamStatus(this, status);
                    }
                    break;

                case "PreviewSceneChanged":
                    PreviewSceneChanged?.Invoke(this, (string)body["scene-name"]);
                    break;

                case "StudioModeSwitched":
                    StudioModeSwitched?.Invoke(this, (bool)body["new-state"]);
                    break;

                case "ReplayStarting":
                    ReplayBufferStateChanged?.Invoke(this, OutputState.Starting);
                    break;

                case "ReplayStarted":
                    ReplayBufferStateChanged?.Invoke(this, OutputState.Started);
                    break;

                case "ReplayStopping":
                    ReplayBufferStateChanged?.Invoke(this, OutputState.Stopping);
                    break;

                case "ReplayStopped":
                    ReplayBufferStateChanged?.Invoke(this, OutputState.Stopped);
                    break;

                case "Exiting":
                    OBSExit?.Invoke(this, EventArgs.Empty);
                    break;

                case "Heartbeat":
                    Heartbeat?.Invoke(this, new Heartbeat(body));
                    break;
                case "SceneItemDeselected":
                    SceneItemDeselected?.Invoke(this, (string)body["scene-name"], (string)body["item-name"], (string)body["item-id"]);
                    break;
                case "SceneItemSelected":
                    SceneItemSelected?.Invoke(this, (string)body["scene-name"], (string)body["item-name"], (string)body["item-id"]);
                    break;
                case "SceneItemTransformChanged":
                    SceneItemTransformChanged?.Invoke(this, new SceneItemTransformInfo(body));
                    break;
                case "SourceAudioMixersChanged":
                    SourceAudioMixersChanged?.Invoke(this, new AudioMixersChangedInfo(body));
                    break;
                case "SourceAudioSyncOffsetChanged":
                    SourceAudioSyncOffsetChanged?.Invoke(this, (string)body["sourceName"], (int)body["syncOffset"]);
                    break;
                case "SourceCreated":
                    SourceCreated?.Invoke(this, new SourceSettings(body));
                    break;
                case "SourceDestroyed":
                    SourceDestroyed?.Invoke(this, (string)body["sourceName"], (string)body["sourceType"], (string)body["sourceKind"]);
                    break;
                case "SourceRenamed":
                    SourceRenamed?.Invoke(this, (string)body["newName"], (string)body["previousName"]);
                    break;
                case "SourceMuteStateChanged":
                    SourceMuteStateChanged?.Invoke(this, (string)body["sourceName"], (bool)body["muted"]);
                    break;
                case "SourceVolumeChanged":
                    SourceVolumeChanged?.Invoke(this, (string)body["sourceName"], (float)body["volume"]);
                    break;
                case "SourceFilterAdded":
                    SourceFilterAdded?.Invoke(this, (string)body["sourceName"], (string)body["filterName"], (string)body["filterType"], (JObject)body["filterSettings"]);
                    break;
                case "SourceFilterRemoved":
                    SourceFilterRemoved?.Invoke(this, (string)body["sourceName"], (string)body["filterName"]);
                    break;
                case "SourceFiltersReordered":
                    List<FilterReorderItem> filters = new List<FilterReorderItem>();
                    JsonConvert.PopulateObject(body["filters"].ToString(), filters);
                    SourceFiltersReordered?.Invoke(this, (string)body["sourceName"], filters);
                    break;
            }
        }

        /// <summary>
        /// Encode a Base64-encoded SHA-256 hash
        /// </summary>
        /// <param name="input">source string</param>
        /// <returns></returns>
        protected string HashEncode(string input)
        {
            using (var sha256 = new SHA256Managed())
            {
                byte[] textBytes = Encoding.ASCII.GetBytes(input);
                byte[] hash = sha256.ComputeHash(textBytes);

                return Convert.ToBase64String(hash);
            }
        }

        /// <summary>
        /// Generate a message ID
        /// </summary>
        /// <param name="length">(optional) message ID length</param>
        /// <returns>A random string of alphanumerical characters</returns>
        protected string NewMessageID(int length = 16)
        {
            const string pool = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var random = new Random();

            string result = "";
            for (int i = 0; i < length; i++)
            {
                int index = random.Next(0, pool.Length - 1);
                result += pool[index];
            }

            return result;
        }

    }
}
