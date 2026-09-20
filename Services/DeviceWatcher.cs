using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AdoxicSound.Services;

/// <summary>Live Windows audio-endpoint notifications (plug/unplug/disable/default).</summary>
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enum = new();
    private bool _registered;

    public event Action? DevicesChanged;
    public event Action<string>? DeviceRemoved;
    public event Action? DeviceAdded;

    public void Start()
    {
        if (_registered) return;
        _enum.RegisterEndpointNotificationCallback(this);
        _registered = true;
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
        DeviceAdded?.Invoke();
        DevicesChanged?.Invoke();
    }

    public void OnDeviceRemoved(string deviceId)
    {
        DeviceRemoved?.Invoke(deviceId);
        DevicesChanged?.Invoke();
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (newState != DeviceState.Active) DeviceRemoved?.Invoke(deviceId);
        DevicesChanged?.Invoke();
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { if (_registered) _enum.UnregisterEndpointNotificationCallback(this); } catch { }
        try { _enum.Dispose(); } catch { }
    }
}
