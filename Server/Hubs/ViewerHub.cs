using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Remotely.Server.Auth;
using Remotely.Server.Models;
using Remotely.Server.Services;
using Remotely.Shared.Enums;
using Remotely.Shared.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Remotely.Server.Hubs
{
    [ServiceFilter(typeof(RemoteControlFilterAttribute))]
    public class ViewerHub : Hub
    {
        public ViewerHub(IDataService dataService,
            IHubContext<CasterHub> casterHubContext,
            IHubContext<AgentHub> agentHub,
            IApplicationConfig appConfig)
        {
            DataService = dataService;
            CasterHubContext = casterHubContext;
            AgentHubContext = agentHub;
            AppConfig = appConfig;
        }

        private IApplicationConfig AppConfig { get; set; }
        private IDataService DataService { get; }

        private RemoteControlMode Mode
        {
            get
            {
                return (RemoteControlMode)Context.Items["Mode"];
            }
            set
            {
                Context.Items["Mode"] = value;
            }
        }

        private IHubContext<CasterHub> CasterHubContext { get; }
        private IHubContext<AgentHub> AgentHubContext { get; }
        private RCSessionInfo SessionInfo
        {
            get
            {
                if (Context.Items.ContainsKey("SessionInfo"))
                {
                    return (RCSessionInfo)Context.Items["SessionInfo"];
                }
                else
                {
                    return null;
                }
            }
            set
            {
                Context.Items["SessionInfo"] = value;
            }
        }
        private string RequesterName
        {
            get
            {
                return Context.Items["RequesterName"] as string;
            }
            set
            {
                Context.Items["RequesterName"] = value;
            }
        }

        private string ScreenCasterID
        {
            get
            {
                return Context.Items["ScreenCasterID"] as string;
            }
            set
            {
                Context.Items["ScreenCasterID"] = value;
            }
        }

        private Guid? PrejoinID
        {
            get
            {
                return Context.Items["PrejoinID"] as Guid?;
            }
            set
            {
                Context.Items["PrejoinID"] = value;
            }
        }

        private string Otp
        {
            get
            {
                return Context.Items["Otp"] as string;
            }
            set
            {
                Context.Items["Otp"] = value;
            }
        }

        public static ConcurrentDictionary<string, List<Guid>> ViewersWaitingForConnection = new();

        public Task ChangeWindowsSession(int sessionID)
        {
            if (SessionInfo?.Mode == RemoteControlMode.Unattended)
            {
                return AgentHubContext.Clients
                    .Client(SessionInfo.ServiceID)
                    .SendAsync("ChangeWindowsSession",
                        SessionInfo.ServiceID,
                        Context.ConnectionId,
                        sessionID);
            }
            return Task.CompletedTask;
        }

        public Task SendDtoToClient(byte[] baseDto)
        {
            if (string.IsNullOrWhiteSpace(ScreenCasterID))
            {
                return Task.CompletedTask;
            }

            return CasterHubContext.Clients.Client(ScreenCasterID).SendAsync("SendDtoToClient", baseDto, Context.ConnectionId);
        }

        public override Task OnConnectedAsync()
        {
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(ScreenCasterID))
            {
                CasterHubContext.Clients.Client(ScreenCasterID).SendAsync("ViewerDisconnected", Context.ConnectionId);
            }

            return base.OnDisconnectedAsync(exception);
        }

        public Task SendIceCandidateToAgent(string candidate, int sdpMlineIndex, string sdpMid)
        {
            return CasterHubContext.Clients.Client(ScreenCasterID).SendAsync("ReceiveIceCandidate", candidate, sdpMlineIndex, sdpMid, Context.ConnectionId);
        }

        public Task SendRtcAnswerToAgent(string sdp)
        {
            return CasterHubContext.Clients.Client(ScreenCasterID).SendAsync("ReceiveRtcAnswer", sdp, Context.ConnectionId);
        }

        public Task WaitForDeviceToConnect(Guid prejoinID)
        {
            PrejoinID = prejoinID;
            Groups.AddToGroupAsync(Context.ConnectionId, prejoinID.ToString());
            return Task.CompletedTask;
        }

        public async Task SendScreenCastRequestToDevice(string screenCasterID, string requesterName, int remoteControlMode, string otp)
        {
            if (string.IsNullOrWhiteSpace(screenCasterID))
            {
                return;
            }

            // if mode is normal, and this was triggered by the remotely ui, then try to resolve to the proper screen caster id
            // since the one passed in is actually the session id.
            // otherwise, continue on and try to find a value with the provided screen caster id (adhoc)
            if ((RemoteControlMode)remoteControlMode == RemoteControlMode.Normal)
            {
                if (CasterHub.SessionInfoList.Any(x => x.Value.AttendedSessionID == screenCasterID))
                {
                    screenCasterID = CasterHub.SessionInfoList.First(x => x.Value.AttendedSessionID == screenCasterID).Value.CasterSocketID;
                }
            }

            if (!CasterHub.SessionInfoList.TryGetValue(screenCasterID, out var sessionInfo))
            {
                await Clients.Caller.SendAsync("SessionIDNotFound");
                return;
            }

            // remove prejoin id
            if (PrejoinID.HasValue)
            {
                if (ViewersWaitingForConnection.TryGetValue(sessionInfo.DeviceID, out var prejoinIds))
                {
                    prejoinIds.Remove(PrejoinID.Value);
                    if (prejoinIds.Count == 0) ViewersWaitingForConnection.Remove(sessionInfo.DeviceID, out _);
                }
            }

            SessionInfo = sessionInfo;
            ScreenCasterID = screenCasterID;
            RequesterName = requesterName;
            Mode = (RemoteControlMode)remoteControlMode;

            string orgId = null;

            if (Context?.User?.Identity?.IsAuthenticated == true)
            {
                var user = DataService.GetUserByID(Context.UserIdentifier);
                if (string.IsNullOrWhiteSpace(RequesterName))
                {
                    RequesterName = user.UserOptions.DisplayName ?? user.UserName;
                }
                orgId = user.OrganizationID;
                var currentUsers = CasterHub.SessionInfoList.Count(x =>
                    x.Key != screenCasterID &&
                    x.Value.OrganizationID == orgId &&
                    x.Value.ViewerList.Any());
                if (currentUsers >= AppConfig.RemoteControlSessionLimit)
                {
                    await Clients.Caller.SendAsync("ShowMessage", "Max number of concurrent sessions reached.");
                    Context.Abort();
                    return;
                }
                SessionInfo.OrganizationID = orgId;
                SessionInfo.RequesterUserName = Context.User.Identity.Name;
                SessionInfo.RequesterSocketID = Context.ConnectionId;
            }

            DataService.WriteEvent(new EventLog()
            {
                EventType = EventType.Info,
                TimeStamp = DateTimeOffset.Now,
                Message = $"Remote control session requested.  " +
                                $"Login ID (if logged in): {Context?.User?.Identity?.Name}.  " +
                                $"Machine Name: {SessionInfo.MachineName}.  " +
                                $"Requester Name (if specified): {RequesterName}.  " +
                                $"Connection ID: {Context.ConnectionId}. User ID: {Context.UserIdentifier}.  " +
                                $"Screen Caster ID: {screenCasterID}.  " +
                                $"Mode: {(RemoteControlMode)remoteControlMode}.  " +
                                $"Requester IP Address: " + Context?.GetHttpContext()?.Connection?.RemoteIpAddress?.ToString(),
                OrganizationID = orgId
            });


            if (Mode == RemoteControlMode.Unattended)
            {
                SessionInfo.Mode = RemoteControlMode.Unattended;
                bool useWebRtc = AppConfig.UseWebRtc;
                if ((!string.IsNullOrWhiteSpace(otp) &&
                        RemoteControlFilterAttribute.OtpMatchesDevice(otp, sessionInfo.DeviceID))
                    ||
                    (Context.User.Identity.IsAuthenticated &&
                        DataService.DoesUserHaveAccessToDevice(sessionInfo.DeviceID, Context.UserIdentifier)))
                {
                    var orgName = DataService.GetOrganizationNameById(orgId);
                    await CasterHubContext.Clients.Client(screenCasterID).SendAsync("GetScreenCast",
                        Context.ConnectionId,
                        RequesterName,
                        AppConfig.RemoteControlNotifyUser,
                        AppConfig.EnforceAttendedAccess,
                        useWebRtc,
                        orgName);
                }
                else
                {
                    await Clients.Caller.SendAsync("Unauthorized");
                }
            }
            else
            {
                SessionInfo.Mode = RemoteControlMode.Normal;
                await Clients.Caller.SendAsync("RequestingScreenCast");
                await CasterHubContext.Clients.Client(screenCasterID).SendAsync("RequestScreenCast", Context.ConnectionId, RequesterName, AppConfig.RemoteControlNotifyUser, AppConfig.UseWebRtc);
            }
        }

    }
}
