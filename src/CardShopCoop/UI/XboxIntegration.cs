using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace CardShopCoop.UI
{
    internal static class XboxIntegration
    {
        private static string _cachedGamertag;
        private static string _cachedLocalIP;

        internal static string Gamertag
        {
            get
            {
                if (!string.IsNullOrEmpty(_cachedGamertag))
                {
                    return _cachedGamertag;
                }

                _cachedGamertag = FindGamertag();
                return _cachedGamertag ?? "Player";
            }
        }

        internal static string LocalIP
        {
            get
            {
                if (!string.IsNullOrEmpty(_cachedLocalIP))
                {
                    return _cachedLocalIP;
                }

                try
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.IP);
                    socket.Connect("8.8.8.8", 65530);
                    if (socket.LocalEndPoint is IPEndPoint endPoint)
                    {
                        _cachedLocalIP = endPoint.Address.ToString();
                        return _cachedLocalIP;
                    }
                }
                catch
                {
                }

                try
                {
                    var addresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
                    foreach (var ip in addresses)
                    {
                        if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        {
                            _cachedLocalIP = ip.ToString();
                            return _cachedLocalIP;
                        }
                    }
                }
                catch
                {
                }

                _cachedLocalIP = "127.0.0.1";
                return _cachedLocalIP;
            }
        }

        private static string FindGamertag()
        {
            try
            {
                var go = GameObject.Find("XboxUserTag");
                if (go != null)
                {
                    var tmp = go.GetComponent<TextMeshProUGUI>();
                    if (tmp != null && !string.IsNullOrEmpty(tmp.text))
                    {
                        return tmp.text.Trim();
                    }
                }

                var pmType = Type.GetType("GA.PlatformManager, Assembly-CSharp");
                if (pmType != null)
                {
                    var instance = pmType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null, null);
                    if (instance != null)
                    {
                        var impl = pmType.GetField("m_platformImpl", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);
                        if (impl != null)
                        {
                            var userMgr = impl.GetType().GetField("m_userManager", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(impl);
                            if (userMgr != null)
                            {
                                var userData = userMgr.GetType().GetProperty("CurrentUserData", BindingFlags.Instance | BindingFlags.Public)?.GetValue(userMgr, null);
                                if (userData != null)
                                {
                                    var gamertag = userData.GetType().GetField("UserGamertag", BindingFlags.Instance | BindingFlags.Public)?.GetValue(userData) as string;
                                    if (!string.IsNullOrEmpty(gamertag))
                                    {
                                        return gamertag.Trim();
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log?.LogWarning("coop: Xbox gamertag lookup: " + ex.Message);
            }

            if (!string.IsNullOrEmpty(CoopPlugin.PlayerName?.Value))
            {
                return CoopPlugin.PlayerName.Value;
            }

            var userName = Environment.UserName;
            if (!string.IsNullOrEmpty(userName))
            {
                return userName;
            }

            return "Player";
        }
    }
}
