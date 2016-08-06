﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using agsXMPP;
using agsXMPP.protocol.client;
using agsXMPP.Sasl;
using agsXMPP.Xml.Dom;
using HarmonyHub.Entities;

namespace HarmonyHub
{
    /// <summary>
    /// Client to interrogate and control Logitech Harmony Hub.
    /// </summary>
    public class HarmonyClient : IDisposable
    {
        // The connection
        private readonly XmppClientConnection _xmpp;
        // A lookup to correlate request and responses
        private readonly IDictionary<string, TaskCompletionSource<IQ>> _resultTaskCompletionSources = new ConcurrentDictionary<string, TaskCompletionSource<IQ>>();

        /// <summary>
        ///  This has the login state..
        ///  When the OnLoginHandler is triggered this is set with true, 
        ///  When an error occurs before this, the expeception is set.
        ///  Everywhere where this is awaited the state is returned, but blocks until there is something.
        /// </summary>
        private readonly TaskCompletionSource<bool> _loginTaskCompletionSource = new TaskCompletionSource<bool>();

        /// <summary>
        /// Constructor with standard settings for a new HarmonyClient
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="port"></param>
        /// <param name="token"></param>
        public HarmonyClient(string ipAddress, int port, string token)
        {
            _xmpp = new XmppClientConnection(ipAddress, port)
            {
                UseStartTLS = false,
                UseSSL = false,
                UseCompression = false,
                AutoResolveConnectServer = false,
                AutoAgents = false,
                AutoPresence = true,
                AutoRoster = true
            };
            // Configure Sasl not to use auto and PLAIN for authentication
            _xmpp.OnSaslStart += SaslStartHandler;
            _xmpp.OnLogin += OnLoginHandler;
            _xmpp.OnIq += OnIqResponseHandler;
            _xmpp.OnSocketError += ErrorHandler;
            // Open the connection, do the login
            _xmpp.Open($"{token}@x.com", token);
        }

        /// <summary>
        /// Cleanup and close
        /// </summary>
        public void Dispose()
        {
            _xmpp.OnIq -= OnIqResponseHandler;
            _xmpp.OnLogin -= OnLoginHandler;
            _xmpp.OnSocketError -= ErrorHandler;
            _xmpp.OnSaslStart -= SaslStartHandler;
            _xmpp.Close();
        }

        #region Event Handlers

        /// <summary>
        /// Configure Sasl not to use auto and PLAIN for authentication
        /// </summary>
        /// <param name="sender">object</param>
        /// <param name="saslEventArgs">SaslEventArgs</param>
        private void SaslStartHandler(object sender, SaslEventArgs saslEventArgs)
        {
            saslEventArgs.Auto = false;
            saslEventArgs.Mechanism = "PLAIN";

        }

        /// <summary>
        /// Handle login by completing the _loginTaskCompletionSource
        /// </summary>
        /// <param name="sender"></param>
        private void OnLoginHandler(object sender)
        {
            _loginTaskCompletionSource.TrySetResult(true);
        }

        /// <summary>
        /// Lookup the TaskCompletionSource for the IQ message and try to set the result.
        /// </summary>
        /// <param name="sender">object</param>
        /// <param name="iq">IQ</param>
        private void OnIqResponseHandler(object sender, IQ iq)
        {
            Debug.WriteLine("Received event " + iq.Id);
            TaskCompletionSource<IQ> resulTaskCompletionSource;
            if (iq.Id != null && _resultTaskCompletionSources.TryGetValue(iq.Id, out resulTaskCompletionSource))
            {
                _resultTaskCompletionSources.Remove(iq.Id);
                resulTaskCompletionSource.TrySetResult(iq);
            }
            else
            {
                Debug.WriteLine("No result task found.");
            }
        }

        /// <summary>
        /// Help with login errors
        /// </summary>
        /// <param name="sender">object</param>
        /// <param name="ex">Exception</param>
        private void ErrorHandler(object sender, Exception ex)
        {
            _loginTaskCompletionSource.TrySetException(ex);
        }

        #endregion

        /// <summary>
        /// Send a document, await the response and return it
        /// </summary>
        /// <param name="document">Document</param>
        /// <returns>IQ response</returns>
        private async Task<IQ> RequestResponseAsync(Document document)
        {
            // Check if the login was made, this blocks until there is a state
            // And throws an exception if the login failed.
            await _loginTaskCompletionSource.Task.ConfigureAwait(false);

            // Create the IQ to send
            var iqToSend = new IQ
            {
                Type = IqType.get,
                Namespace = "",
                From = "1",
                To = "guest"
            };

            // Add the real content for the Harmony
            iqToSend.AddChild(document);

            // Generate an unique ID, this is used to correlate the reply to the request
            iqToSend.GenerateId();

            // Prepate the TaskCompletionSource, which is used to await the result
            var resultTaskCompletionSource = new TaskCompletionSource<IQ>();
            _resultTaskCompletionSources[iqToSend.Id] = resultTaskCompletionSource;

            // Start the sending
            _xmpp.Send(iqToSend);

            // Await / block until an reply arrives or the timeout happens
            return await resultTaskCompletionSource.Task.ConfigureAwait(false);
        }

        #region Authentication
        /// <summary>
        /// Send message to HarmonyHub with UserAuthToken, wait for SessionToken
        /// </summary>
        /// <param name="userAuthToken"></param>
        /// <returns></returns>
        public async Task<string> SwapAuthToken(string userAuthToken)
        {
            var iq = await RequestResponseAsync(HarmonyDocuments.LogitechPairDocument(userAuthToken)).ConfigureAwait(false);
            var sessionData = GetData(iq);
            if (sessionData != null)
            {
                foreach (var pair in sessionData.Split(':'))
                {
                    if (pair.StartsWith("identity"))
                    {
                        return pair.Split('=')[1];
                    }
                }
            }
            throw new Exception("Wrong data");
        }
        #endregion


        #region Send Messages to HarmonyHub

        /// <summary>
        /// Request the configuration from the hub
        /// </summary>
        /// <returns>HarmonyConfig</returns>
        public async Task<Config> GetConfigAsync()
        {
            var iq = await RequestResponseAsync(HarmonyDocuments.ConfigDocument()).ConfigureAwait(false);
            var config = GetData(iq);
            if (config != null)
            {
                return new JavaScriptSerializer().Deserialize<Config>(config);
            }
            throw new Exception("Wrong data");
        }

        /// <summary>
        /// Send message to HarmonyHub to start a given activity
        /// Result is parsed by OnIq based on ClientCommandType
        /// </summary>
        /// <param name="activityId"></param>
        public async Task StartActivityAsync(string activityId)
        {
            var iq = await RequestResponseAsync(HarmonyDocuments.StartActivityDocument(activityId)).ConfigureAwait(false);
            if (iq.Error != null)
            {
                throw new Exception(iq.Error.ErrorText);
            }
        }

        /// <summary>
        /// Send message to HarmonyHub to request current activity
        /// Result is parsed by OnIq based on ClientCommandType
        /// </summary>
        public async Task<string> GetCurrentActivityAsync()
        {
            var iq = await RequestResponseAsync(HarmonyDocuments.GetCurrentActivityDocument()).ConfigureAwait(false);
            var currentActivityData = GetData(iq);
            if (currentActivityData != null)
            {
                return currentActivityData.Split('=')[1];
            }
            throw new Exception("Wrong data");
        }

        /// <summary>
        /// Send message to HarmonyHub to request to press a button
        /// Result is parsed by OnIq based on ClientCommandType
        /// </summary>
        /// <param name="deviceId"></param>
        /// <param name="command"></param>
        public async Task PressButtonAsync(string deviceId, string command)
        {
            var iq = await RequestResponseAsync(HarmonyDocuments.IrCommandDocument(deviceId, command)).ConfigureAwait(false);
            if (iq.Error != null)
            {
                throw new Exception(iq.Error.ErrorText);
            }
            throw new Exception("Wrong data");
        }

        /// <summary>
        /// Send message to HarmonyHub to request to turn off all devices
        /// </summary>
        public async Task TurnOffAsync()
        {
            var currentActivity = await GetCurrentActivityAsync().ConfigureAwait(false);
            if (currentActivity != "-1")
            {
                await StartActivityAsync("-1").ConfigureAwait(false);
            }
        }

        #endregion

        /// <summary>
        /// Get the data from the IQ response object
        /// </summary>
        /// <param name="iq">IQ response object</param>
        /// <returns>string with the data of the element</returns>
        private string GetData(IQ iq)
        {
            if (iq.HasTag("oa"))
            {
                var oaElement = iq.SelectSingleElement("oa");
                // Keep receiving messages until we get a 200 status
                // Activity commands send 100 (continue) until they finish
                var errorCode = oaElement.GetAttribute("errorcode");
                if ("200".Equals(errorCode))
                {
                    return oaElement.GetData();
                }
            }
            return null;
        }
    }
}