﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using CommandLine;
using HarmonyHub;

namespace HarmonyConsole
{
    public class Program
    {
        public static void Main(string[] args)
        {
            const int harmonyPort = 5222;
            var options = new Options();
            if (!Parser.Default.ParseArguments(args, options))
            {
                return;
            }
            Console.WriteLine();

            string ipAddress = options.IpAddress;
            string username = options.Username;
            string password = options.Password;

            string deviceId = options.DeviceId;
            string activityId = options.ActivityId;

            string sessionToken;

            if (File.Exists("SessionToken"))
            {
                sessionToken = File.ReadAllText("SessionToken");
                Console.WriteLine("Reusing token: {0}", sessionToken);
            }
            else
            {
                sessionToken = LoginToLogitech(username, password, ipAddress, harmonyPort);
            }

            // do we need to grab the config first?
            HarmonyConfigResult harmonyConfig = null;

            HarmonyClient client = null;

            if (!string.IsNullOrEmpty(deviceId) || options.GetActivity || !string.IsNullOrEmpty(options.ListType))
            {
                client = new HarmonyClient(ipAddress, harmonyPort, sessionToken);
                client.GetConfig();

                while (string.IsNullOrEmpty(client.Config))
                {
                }
                File.WriteAllText("HubConfig", client.Config);
                harmonyConfig = new JavaScriptSerializer().Deserialize<HarmonyConfigResult>(client.Config);
            }

            if (!string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(options.Command))
            {
                if (null == client)
                {
                    client = new HarmonyClient(ipAddress, harmonyPort, sessionToken);
                }
                //activityClient.PressButton("14766260", "Mute");
                client.PressButton(deviceId, options.Command);
            }

            if (null != harmonyConfig && !string.IsNullOrEmpty(deviceId) && string.IsNullOrEmpty(options.Command))
            {
                // just list device control options
                foreach (var device in harmonyConfig.device.Where(device => device.id == deviceId))
                {
                    foreach (Dictionary<string, object> controlGroup in device.controlGroup)
                    {
                        foreach (var o in controlGroup.Where(o => o.Key == "name"))
                        {
                            Console.WriteLine($"{o.Key}:{o.Value}");
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(activityId))
            {
                if (null == client)
                {
                    client = new HarmonyClient(ipAddress, harmonyPort, sessionToken);
                }
                client.StartActivity(activityId);
            }

            if (null != harmonyConfig && options.GetActivity)
            {
                client.GetCurrentActivity();
                // now wait for it to be populated
                while (string.IsNullOrEmpty(client.CurrentActivity))
                {
                }
                Console.WriteLine("Current Activity: {0}", harmonyConfig.ActivityNameFromId(client.CurrentActivity));
            }

            if (options.TurnOff)
            {
                if (null == client)
                {
                    client = new HarmonyClient(ipAddress, harmonyPort, sessionToken);
                }
                client.TurnOff();
            }

            if (null != harmonyConfig && !string.IsNullOrEmpty(options.ListType))
            {
                if (!options.ListType.Equals("d") && !options.ListType.Equals("a")) return;

                if (options.ListType.Equals("a"))
                {
                    Console.WriteLine("Activities:");
                    harmonyConfig.activity.Sort();
                    foreach (var activity in harmonyConfig.activity)
                    {
                        Console.WriteLine(" {0}:{1}", activity.id, activity.label);
                    }
                }

                if (options.ListType.Equals("d"))
                {
                    Console.WriteLine("Devices:");
                    harmonyConfig.device.Sort();
                    foreach (var device in harmonyConfig.device)
                    {
                        Console.WriteLine($" {device.id}:{device.label}");
                    }
                }
            }
        }

        public static string LoginToLogitech(string email, string password, string ipAddress, int harmonyPort)
        {
            string userAuthToken = HarmonyLogin.GetUserAuthToken(email, password);
            if (string.IsNullOrEmpty(userAuthToken))
            {
                throw new Exception("Could not get token from Logitech server.");
            }

            File.WriteAllText("UserAuthToken", userAuthToken);

            var authentication = new HarmonyAuthenticationClient(ipAddress, harmonyPort);

            string sessionToken = authentication.SwapAuthToken(userAuthToken);
            if (string.IsNullOrEmpty(sessionToken))
            {
                throw new Exception("Could not swap token on Harmony Hub.");
            }

            File.WriteAllText("SessionToken", sessionToken);

            Console.WriteLine("Date Time : {0}", DateTime.Now);
            Console.WriteLine("User Token: {0}", userAuthToken);
            Console.WriteLine("Sess Token: {0}", sessionToken);

            return sessionToken;
        }
    }
}