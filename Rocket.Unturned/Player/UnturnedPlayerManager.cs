﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rocket.API;
using Rocket.API.Commands;
using Rocket.API.DependencyInjection;
using Rocket.API.Eventing;
using Rocket.API.Player;
using Rocket.API.User;
using Rocket.Core.Logging;
using Rocket.Core.Player;
using Rocket.Core.Player.Events;
using Rocket.Core.User;
using Rocket.Core.User.Events;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using Color = UnityEngine.Color;
using ILogger = Rocket.API.Logging.ILogger;

namespace Rocket.Unturned.Player
{
    public class UnturnedPlayerManager : IPlayerManager
    {
        private readonly IHost host;
        private readonly IEventManager eventManager;
        private readonly IDependencyContainer container;
        private readonly ILogger logger;

        public UnturnedPlayerManager(IHost host, IEventManager @eventManager,
                                     IDependencyContainer container, ILogger logger)
        {
            this.host = host;
            this.eventManager = eventManager;
            this.container = container;
            this.logger = logger;
        }

        public bool Kick(IUser target, IUser kicker = null, string reason = null)
        {
            var player = ((UnturnedUser)target).Player;
            PlayerKickEvent @event = new PlayerKickEvent(player, kicker, reason, true);
            eventManager.Emit(host, @event);
            if (@event.IsCancelled)
                return false;

            Provider.kick((((UnturnedUser)target).Player).CSteamID, reason);
            return true;
        }

        public bool Ban(IUserInfo target, IUser bannedBy = null, string reason = null, TimeSpan? duration = null)
        {
            //todo: this is wrong
            var player = ((UnturnedUser)target).Player;

            PlayerBanEvent @event = new PlayerBanEvent(player.User, bannedBy, reason, duration, true);
            eventManager.Emit(host, @event);
            if (@event.IsCancelled)
                return false;

            if (target is IUser u && u.IsOnline)
            {
                var uPlayer = ((UnturnedUser)target).Player;
                Provider.ban(uPlayer.CSteamID, reason, (uint)(duration?.TotalSeconds ?? uint.MaxValue));
                return true;
            }

            var steamId = new CSteamID(ulong.Parse(target.Id));
            var callerId = (bannedBy is UnturnedUser up) ? up.Player.CSteamID : CSteamID.Nil;
            SteamBlacklist.ban(steamId, 0, callerId, reason, (uint)(duration?.TotalSeconds ?? uint.MaxValue));
            return true;
        }

        public bool Unban(IUserInfo target, IUser bannedBy = null)
        {
            var player = ((UnturnedUser)target).Player;

            PlayerUnbanEvent @event = new PlayerUnbanEvent(player.User, bannedBy);
            eventManager.Emit(host, @event);
            if (@event.IsCancelled)
                return false;

            var steamId = new CSteamID(ulong.Parse(target.Id));
            return SteamBlacklist.unban(steamId);
        }

        public void SendMessage(IUser sender, IUser receiver, string message, System.Drawing.Color? color = null, params object[] arguments)
        {
            var uColor = color == null
                ? Color.white
                : new Color((1f / 255f) * color.Value.R, (1f / 255f) * color.Value.G, (1f / 255f) * color.Value.B, (1f / 100f) * color.Value.A);

            if (receiver is IConsole console)
            {
                console.WriteLine(message, color, arguments);
                return;
            }

            if (!(receiver is UnturnedUser uuser))
                throw new Exception("Could not cast " + receiver.GetType().FullName + " to UnturnedUser!");

            ChatManager.say(uuser.Player.CSteamID, message, uColor);
        }

        public void Broadcast(IUser sender, string message, System.Drawing.Color? color = null, params object[] arguments)
        {
            Broadcast(sender, OnlineUsers, message, color, arguments);
            logger.LogInformation("[Broadcast] " + message);
        }

        public IUserInfo GetUser(string id)
        {
            if (TryGetOnlinePlayer(id, out var player))
                return player.GetUser();

            return new OfflineUnturnedUserInfo(this, GetPlayer(id));
        }

        public void Broadcast(IUser sender, IEnumerable<IUser> receivers, string message,
                              System.Drawing.Color? color = null, params object[] arguments)
        {
            var wrappedMessage = WrapMessage(string.Format(message, arguments));
            foreach (IUser player in receivers)
                foreach (var line in wrappedMessage)
                    player.SendMessage(line, color);
        }

        public System.Drawing.Color? GetColorFromName(string colorName)
        {
            switch (colorName.Trim().ToLower())
            {
                case "black": return System.Drawing.Color.Black;
                case "blue": return System.Drawing.Color.Blue;
                case "cyan": return System.Drawing.Color.Cyan;
                case "gray": return System.Drawing.Color.Gray;
                case "green": return System.Drawing.Color.Green;
                case "grey": return System.Drawing.Color.Gray;
                case "magenta": return System.Drawing.Color.Magenta;
                case "red": return System.Drawing.Color.Red;
                case "white": return System.Drawing.Color.White;
                case "yellow": return System.Drawing.Color.Yellow;
                case "rocket": return GetColorFromRGB(90, 206, 205);
            }

            return GetColorFromHex(colorName);
        }

        public System.Drawing.Color? GetColorFromHex(string hexString)
        {
            hexString = hexString.Replace("#", "");
            if (hexString.Length == 3)
            {                                                                           // #99f
                hexString = hexString.Insert(1, System.Convert.ToString(hexString[0])); // #999f
                hexString = hexString.Insert(3, System.Convert.ToString(hexString[2])); // #9999f
                hexString = hexString.Insert(5, System.Convert.ToString(hexString[4])); // #9999ff
            }

            if (hexString.Length != 6 || !int.TryParse(hexString, System.Globalization.NumberStyles.HexNumber, null, out int argb))
            {
                return null;
            }
            byte r = (byte)((argb >> 16) & 0xff);
            byte g = (byte)((argb >> 8) & 0xff);
            byte b = (byte)(argb & 0xff);
            return GetColorFromRGB(r, g, b);
        }

        public System.Drawing.Color GetColorFromRGB(byte R, byte G, byte B)
        {
            return GetColorFromRGB(R, G, B, 100);
        }

        public System.Drawing.Color GetColorFromRGB(byte R, byte G, byte B, short A)
        {
            return System.Drawing.Color.FromArgb(A, R, G, B);
        }

        public static List<string> WrapMessage(string text)
        {
            if (text.Length == 0) return new List<string>();
            string[] words = text.Split(' ');
            List<string> lines = new List<string>();
            string currentLine = "";
            int maxLength = 90;
            foreach (string currentWord in words)
            {
                if (currentLine.Length > maxLength || currentLine.Length + currentWord.Length > maxLength)
                {
                    lines.Add(currentLine);
                    currentLine = "";
                }

                if (currentLine.Length > 0)
                    currentLine += " " + currentWord;
                else
                    currentLine += currentWord;
            }

            if (currentLine.Length > 0)
                lines.Add(currentLine);
            return lines;
        }

        public IEnumerable<IUser> OnlineUsers => OnlinePlayers.Select(c => (IUser)c.GetUser());

        public IPlayer GetPlayer(string id)
        {
            if (TryGetOnlinePlayer(id, out var player))
                return player;

            if (!ulong.TryParse(id, out ulong cId))
                throw new PlayerNameNotFoundException(id);

            return new UnturnedPlayer(container, new CSteamID(cId), this);
        }

        public IPlayer GetOnlinePlayer(string nameOrId)
        {
            SteamPlayer player;

            if (ulong.TryParse(nameOrId, out var id))
                player = PlayerTool.getSteamPlayer(new CSteamID(id));
            else
                player = PlayerTool.getSteamPlayer(nameOrId);

            if (player == null)
                throw new PlayerNotOnlineException(nameOrId);

            return new UnturnedPlayer(container, player, this);
        }

        public IPlayer GetOnlinePlayerByName(string displayName)
        {
            SteamPlayer player = PlayerTool.getSteamPlayer(displayName);
            if (player == null)
                return null;

            return new UnturnedPlayer(container, player, this);
        }

        public IPlayer GetOnlinePlayerById(string id)
        {
            var player = PlayerTool.getSteamPlayer(new CSteamID(ulong.Parse(id)));
            if (player == null)
                return null;

            return new UnturnedPlayer(container, player, this);
        }


        public PreConnectUnturnedPlayer GetPendingPlayer(string uniqueID)
        {
            return PendingPlayers.FirstOrDefault(c => c.Id.Equals(uniqueID));
        }

        public PreConnectUnturnedPlayer GetPendingPlayerByName(string displayName)
        {
            return PendingPlayers.FirstOrDefault(c => c.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase));
        }

        public bool TryGetOnlinePlayer(string nameOrId, out IPlayer output)
        {
            output = null;
            try
            {
                output = GetOnlinePlayer(nameOrId);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool TryGetOnlinePlayerById(string id, out IPlayer output)
        {
            output = null;
            try
            {
                output = GetOnlinePlayerById(id);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool TryGetOnlinePlayerByName(string displayName, out IPlayer output)
        {
            output = null;
            try
            {
                output = GetOnlinePlayerByName(displayName);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Online players which succesfully joined the server.
        /// </summary>
        public IEnumerable<IPlayer> OnlinePlayers =>
            Provider.clients.Select(c => (IPlayer)new UnturnedPlayer(container, c, this));

        /// <summary>
        /// Players which are not authenticated and have not joined yet.
        /// </summary>
        public IEnumerable<PreConnectUnturnedPlayer> PendingPlayers => Provider.pending.Select(c => new PreConnectUnturnedPlayer(container, c, this));
        public string ServiceName => "Unturned";
    }
}