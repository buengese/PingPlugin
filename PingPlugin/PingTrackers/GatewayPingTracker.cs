using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Logging;
using PingPlugin.GameAddressDetectors;

namespace PingPlugin.PingTrackers
{
    public class GatewayPingTracker : IDisposable
    {
        private readonly CancellationTokenSource tokenSource;
        private readonly GatewayAddressDetector addressDetector;
        private readonly GameAddressDetector gameAddressDetector;
        protected readonly PingConfiguration config;
        
        public bool Verbose { get; set; } = true;
        public bool Errored { get; private set; }
        public bool Reset { get; set; }
        public double AverageRTT { get; private set; }
        public IPAddress GatewayAddress { get; private set; }
        public ulong LastRTT { get; private set; }
        public ConcurrentQueue<float> RTTTimes { get; private set; }

        public delegate void PingUpdatedDelegate(PingStatsPayload payload);
        public event PingTracker.PingUpdatedDelegate OnPingUpdated;
        
        public GatewayPingTracker(PingConfiguration config, GatewayAddressDetector addressDetector, GameAddressDetector gameAddressDetector)
        {
            this.tokenSource = new CancellationTokenSource();
            this.config = config;
            this.addressDetector = addressDetector;
            this.gameAddressDetector = gameAddressDetector;

            GatewayAddress = IPAddress.Loopback;
            RTTTimes = new ConcurrentQueue<float>();
        }
        
        public virtual void Start()
        {
            Task.Run(() => PingLoop(this.tokenSource.Token));
        }

        protected void NextRTTCalculation(ulong nextRTT)
        {
            lock (RTTTimes)
            {
                RTTTimes.Enqueue(nextRTT);

                while (RTTTimes.Count > this.config.PingQueueSize)
                    RTTTimes.TryDequeue(out _);
            }
            CalcAverage();

            LastRTT = nextRTT;
            SendMessage();
        }
        
        protected void CalcAverage() => AverageRTT = RTTTimes.Count > 1 ? RTTTimes.Average() : 0;
        
        protected virtual void ResetRTT()
        {
            RTTTimes = new ConcurrentQueue<float>();
            AverageRTT = 0;
            LastRTT = 0;
        }

        private async Task PingLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (GatewayAddress != null)
                {
                    try
                    {
                        var rtt = GetAddressLastRTT(GatewayAddress);
                        var error = (WinError)Marshal.GetLastWin32Error();

                        Errored = error != WinError.NO_ERROR;

                        if (!Errored)
                        {
                            NextRTTCalculation(rtt);
                        }
                        else
                        {
                            PluginLog.LogWarning($"Got Win32 error {error} when executing ping - this may be temporary and acceptable.");
                        }
                    }
                    catch (Exception e)
                    {
                        Errored = true;
                        PluginLog.LogError(e, "Error occurred when executing ping.");
                    }
                }

                await Task.Delay(3000, token);
            }
        }
        
        private async Task AddressUpdateLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var lastAddress = GatewayAddress;

                var serverAddress = this.gameAddressDetector.GetAddress(this.Verbose);
                try
                {
                    GatewayAddress = this.addressDetector.GetAddress(serverAddress, this.Verbose);
                }
                catch (Exception e)
                {
                    PluginLog.LogError(e, "Exception thrown in address detection function.");
                }

                if (!Equals(lastAddress, GatewayAddress))
                {
                    Reset = true;
                    ResetRTT();
                }
                else
                {
                    Reset = false;
                }
                await Task.Delay(10000, token); // It's probably not that expensive, but it's not like the address is constantly changing, either.
            }
        }
        
        private void SendMessage()
        {
            var del = OnPingUpdated;
            del?.Invoke(new PingStatsPayload
            {
                AverageRTT = Convert.ToUInt64(AverageRTT),
                LastRTT = LastRTT,
            });
        }

        private static ulong GetAddressLastRTT(IPAddress address)
        {
            var addressBytes = address.GetAddressBytes();
            var addressRaw = BitConverter.ToUInt32(addressBytes);

            var hopCount = 0U;
            var rtt = 0U;

            return GetRTTAndHopCount(addressRaw, ref hopCount, 51, ref rtt) == 1 ? rtt : 0;
        }

        [DllImport("Iphlpapi.dll", EntryPoint = "GetRTTAndHopCount", SetLastError = true)]
        private static extern int GetRTTAndHopCount(uint address, ref uint hopCount, uint maxHops, ref uint rtt);

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.tokenSource.Cancel();
                this.tokenSource.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}