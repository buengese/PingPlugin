using Dalamud.Logging;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using PingPlugin.Attributes;
using PingPlugin.GameAddressDetectors;
using PingPlugin.PingTrackers;
using System;
using System.Dynamic;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Network;

namespace PingPlugin
{
    public class PingPlugin : IDalamudPlugin
    {
        private readonly DalamudPluginInterface pluginInterface;
        private readonly GameNetwork network;

        private readonly PluginCommandManager<PingPlugin> pluginCommandManager;
        private readonly PingConfiguration config;

        private readonly GameAddressDetector gameAddressDetector;
        private readonly GatewayAddressDetector gatewayAddressDetector;
        private readonly PingUI ui;

        private PingTracker gamePingTracker;
        private readonly GatewayPingTracker gatewayPingTracker;

        private ICallGateProvider<object, object> ipcProvider;

        public string Name => "PingPlugin";

        public PingPlugin(DalamudPluginInterface pluginInterface, CommandManager commands, DtrBar dtrBar, GameNetwork network)
        {
            this.pluginInterface = pluginInterface;
            this.network = network;
            
            this.config = (PingConfiguration)this.pluginInterface.GetPluginConfig() ?? new PingConfiguration();
            this.config.Initialize(this.pluginInterface);

            this.gameAddressDetector = this.pluginInterface.Create<AggregateAddressDetector>();
            if (this.gameAddressDetector == null)
            {
                throw new InvalidOperationException("Failed to create game address detector. The provided arguments may be incorrect.");
            }

            this.gatewayAddressDetector = new();
            
            this.gamePingTracker = RequestNewPingTracker(this.config.TrackingMode);
            this.gamePingTracker.Verbose = false;
            this.gamePingTracker.Start();

            this.gatewayPingTracker = new GatewayPingTracker(this.config, this.gatewayAddressDetector, this.gameAddressDetector);
            this.gatewayPingTracker.Verbose = false;
            this.gatewayPingTracker.Start();

            InitIpc();

            // Most of these can't be created using service injection because the service container only checks ctors for
            // exact types.
            this.ui = new PingUI(this.gamePingTracker, this.gatewayPingTracker, this.pluginInterface, dtrBar, this.config, RequestNewPingTracker);
            this.gamePingTracker.OnPingUpdated += this.ui.UpdateDtrBarGamePing;

            this.pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
            this.pluginInterface.UiBuilder.Draw += this.ui.Draw;

            this.pluginCommandManager = new PluginCommandManager<PingPlugin>(this, commands);
        }

        private PingTracker RequestNewPingTracker(PingTrackerKind kind)
        {
            this.gamePingTracker?.Dispose();
            
            PingTracker newTracker = kind switch
            {
                PingTrackerKind.Aggregate => new AggregatePingTracker(this.config, this.gameAddressDetector, this.network),
                PingTrackerKind.COM => new ComponentModelPingTracker(this.config, this.gameAddressDetector),
                PingTrackerKind.IpHlpApi => new IpHlpApiPingTracker(this.config, this.gameAddressDetector),
                PingTrackerKind.Packets => new PacketPingTracker(this.config, this.gameAddressDetector, this.network),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

            this.gamePingTracker = newTracker;
            if (this.gamePingTracker == null)
            {
                throw new InvalidOperationException($"Failed to create ping tracker \"{kind}\". The provided arguments may be incorrect.");
            }
            
            this.gamePingTracker.Start();
            
            return newTracker;
        }

        private void InitIpc()
        {
            try
            {
                ipcProvider = this.pluginInterface.GetIpcProvider<object, object>("PingPlugin.Ipc");
                this.gamePingTracker.OnPingUpdated += payload =>
                {
                    dynamic obj = new ExpandoObject();
                    obj.LastRTT = payload.LastRTT;
                    obj.AverageRTT = payload.AverageRTT;
                    ipcProvider.SendMessage(obj);
                };
            }
            catch (Exception e)
            {
                PluginLog.Error($"Error registering IPC provider:\n{e}");
            }
        }

        [Command("/ping")]
        [HelpMessage("Show/hide the ping monitor.")]
        [ShowInHelp]
        public void PingCommand(string command, string args)
        {
            this.config.MonitorIsVisible = !this.config.MonitorIsVisible;
            this.config.Save();
        }

        [Command("/pinggraph")]
        [HelpMessage("Show/hide the ping graph.")]
        [ShowInHelp]
        public void PingGraphCommand(string command, string args)
        {
            this.config.GraphIsVisible = !this.config.GraphIsVisible;
            this.config.Save();
        }

        [Command("/pingconfig")]
        [HelpMessage("Show PingPlugin's configuration.")]
        [ShowInHelp]
        public void PingConfigCommand(string command, string args)
        {
            this.ui.ConfigVisible = true;
        }

        private void OpenConfigUi()
        {
            this.ui.ConfigVisible = true;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            
            this.pluginCommandManager.Dispose();

            this.pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
            this.pluginInterface.UiBuilder.Draw -= this.ui.Draw;

            this.config.Save();

            this.gamePingTracker.OnPingUpdated -= this.ui.UpdateDtrBarGamePing;
            this.ui.Dispose();

            this.gatewayPingTracker.Dispose();
            this.gamePingTracker.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
