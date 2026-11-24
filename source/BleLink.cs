using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace LovenseConnector
{
    internal static class WinAsync
    {
        public static Task<T> Task<T>(IAsyncOperation<T> operation)
        {
            var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            operation.Completed = (op, status) => {
                try { source.TrySetResult(op.GetResults()); }
                catch (Exception ex) { source.TrySetException(ex); }
            };
            return source.Task;
        }
    }

    internal sealed class BleScanner : IDisposable
    {
        readonly BluetoothLEAdvertisementWatcher watcher = new BluetoothLEAdvertisementWatcher();
        public event Action<ulong,string,int> Found;
        public BleScanner()
        {
            watcher.ScanningMode = BluetoothLEScanningMode.Active;
            watcher.Received += (sender, args) => {
                var name = args.Advertisement.LocalName;
                if (!String.IsNullOrEmpty(name) && (name.StartsWith("LVS-",StringComparison.OrdinalIgnoreCase) || name.StartsWith("LOVE-",StringComparison.OrdinalIgnoreCase)))
                    if (Found != null) Found(args.BluetoothAddress, name, args.RawSignalStrengthInDBm);
            };
        }
        public async Task<bool> Available() { var adapter = await WinAsync.Task(BluetoothAdapter.GetDefaultAsync()); return adapter != null && adapter.IsLowEnergySupported; }
        public void Start() { watcher.Start(); }
        public void Stop() { if(watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop(); }
        public void Dispose() { Stop(); }
    }

    internal interface IDeviceLink : IDisposable, ICommandLink
    {
        bool Connected { get; }
        Task<string> Query(string command,Func<string,bool> match);
    }

    internal sealed class BleLink : IDeviceLink, ILatestCommandLink
    {
        BluetoothLEDevice device;
        GattDeviceService service;
        GattCharacteristic tx, rx;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1,1);
        readonly object sync = new object();
        readonly ReplyFramer incoming = new ReplyFramer();
        TaskCompletionSource<string> reply;
        Func<string,bool> accepts;
        bool disposed;
        public bool Connected { get { return !disposed && device != null && device.ConnectionStatus == BluetoothConnectionStatus.Connected; } }
        public event Action Lost;
        public event Action<string> Trace;
        public double LastRoundTripMs { get; private set; }
        public bool EdgeMotorEcho { get; set; }

        static bool IsUart(Guid id)
        {
            var value = id.ToString();
            return value == "0000fff0-0000-1000-8000-00805f9b34fb" || value == "6e400001-b5a3-f393-e0a9-e50e24dcca9e" || value.EndsWith("-4bd4-bbd5-a6920e4c5653");
        }
        public static async Task<BleLink> Open(ulong address)
        {
            var link = new BleLink();
            try
            {
                link.device = await WinAsync.Task(BluetoothLEDevice.FromBluetoothAddressAsync(address));
                if(link.device == null) throw new InvalidOperationException("Windows could not open this toy. Close other Bluetooth toy apps and retry.");
                var result = await WinAsync.Task(link.device.GetGattServicesAsync(BluetoothCacheMode.Uncached));
                if(result.Status != GattCommunicationStatus.Success) throw new InvalidOperationException("Service discovery: " + result.Status);
                foreach(var candidate in result.Services)
                {
                    if(link.service != null || !IsUart(candidate.Uuid)) { candidate.Dispose(); continue; }
                    var chars = await WinAsync.Task(candidate.GetCharacteristicsAsync(BluetoothCacheMode.Uncached));
                    if(chars.Status != GattCommunicationStatus.Success) { candidate.Dispose(); continue; }
                    var tx = chars.Characteristics.FirstOrDefault(c => (c.CharacteristicProperties & (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) != 0);
                    var rx = chars.Characteristics.FirstOrDefault(c => (c.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0);
                    if(tx == null || rx == null) { candidate.Dispose(); continue; }
                    link.service = candidate; link.tx = tx; link.rx = rx;
                }
                if(link.service == null) throw new InvalidOperationException("The toy's command service is unavailable. Close other apps using this toy and reconnect.");
                link.rx.ValueChanged += link.Received;
                var subscribed = await WinAsync.Task(link.rx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify));
                if(subscribed != GattCommunicationStatus.Success) throw new InvalidOperationException("Response subscription: " + subscribed);
                link.device.ConnectionStatusChanged += link.StatusChanged;
                return link;
            }
            catch { link.Dispose(); throw; }
        }
        void StatusChanged(BluetoothLEDevice sender, object args)
        {
            if(!disposed && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                lock(sync) if(reply != null) reply.TrySetException(new InvalidOperationException("Bluetooth connection lost."));
                if(Lost != null) Lost();
            }
        }
        void Received(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                using(var reader = DataReader.FromBuffer(args.CharacteristicValue))
                {
                    var bytes = new byte[reader.UnconsumedBufferLength]; reader.ReadBytes(bytes);
                    lock(sync)
                    {
                        foreach(string line in incoming.Append(Encoding.ASCII.GetString(bytes)))
                        {
                            if(reply != null && (IsError(line) || accepts == null || accepts(line))) reply.TrySetResult(line);
                            else if(Trace!=null) Trace("Unmatched reply: " + line);
                        }
                    }
                }
            }
            catch(Exception ex) { lock(sync) if(reply != null) reply.TrySetException(ex); }
        }
        public static bool IsError(string value) { return Replies.IsError(value); }
        public async Task<string> Request(string command, Func<string,bool> match, CancellationToken cancel,Func<string> latest=null)
        {
            await gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                cancel.ThrowIfCancellationRequested();
                if(disposed) throw new ObjectDisposedException("Bluetooth connection");
                if(latest!=null) { command=latest();if(command==null)return "";match=value=>Replies.CommandMatches(command,value,EdgeMotorEcho); }
                var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock(sync) { reply = pending; accepts = match; }
                string description=ProtocolDiagnostics.Command(command);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                if(Trace!=null) Trace("TX " + description);
                using(var writer = new DataWriter())
                {
                    writer.WriteBytes(Encoding.ASCII.GetBytes(command));
                    var write = WinAsync.Task(tx.WriteValueAsync(writer.DetachBuffer(), (tx.CharacteristicProperties & GattCharacteristicProperties.Write) != 0 ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse));
                    if(await System.Threading.Tasks.Task.WhenAny(write,System.Threading.Tasks.Task.Delay(3000)).ConfigureAwait(false) != write)
                    { Dispose(); throw new TimeoutException("Bluetooth write timed out for " + description + " Connection released."); }
                    var status = await write.ConfigureAwait(false);
                    if(status != GattCommunicationStatus.Success) { Dispose(); throw new InvalidOperationException("Bluetooth write: " + status + " for " + description); }
                }
                // Once a command is written, consume its reply before allowing the next command.
                // Cancellation is checked BEFORE the write, so ACKs cannot slip to another request.
                if(await System.Threading.Tasks.Task.WhenAny(pending.Task,System.Threading.Tasks.Task.Delay(1200)).ConfigureAwait(false) != pending.Task)
                { Dispose(); throw new TimeoutException("Toy did not acknowledge " + description + " Connection released."); }
                string response = await pending.Task.ConfigureAwait(false);
                LastRoundTripMs = timer.Elapsed.TotalMilliseconds;
                if(Trace != null) Trace(description + " → " + response + " (" + Math.Round(LastRoundTripMs) + " ms)");
                if(IsError(response)) throw new NotSupportedException("Toy rejected " + description + ": " + response);
                return response;
            }
            finally { lock(sync) { reply = null; accepts = null; } gate.Release(); }
        }
        public Task<string> Command(string command, CancellationToken cancel) { return Request(command, value => Replies.CommandMatches(command,value,EdgeMotorEcho), cancel); }
        public Task<string> CommandLatest(Func<string> command,CancellationToken cancel) { return Request(null,null,cancel,command); }
        public Task<string> Command(string command) { return Command(command,CancellationToken.None); }
        public Task<string> Query(string command, Func<string,bool> match) { return Request(command, match, CancellationToken.None); }
        public void Dispose()
        {
            if(disposed) return; disposed = true;
            lock(sync) if(reply != null) reply.TrySetException(new ObjectDisposedException("Bluetooth connection"));
            try { if(rx != null) rx.ValueChanged -= Received; } catch { }
            try { if(device != null) device.ConnectionStatusChanged -= StatusChanged; } catch { }
            try { if(service != null) service.Dispose(); } catch { }
            try { if(device != null) device.Dispose(); } catch { }
        }
    }
}
