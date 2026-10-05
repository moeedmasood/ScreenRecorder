using NAudio.CoreAudioApi;

namespace ScreenRecorder.Capture;

public sealed record AudioDeviceInfo(string Id, string Name);

public static class AudioDevices
{
    public static List<AudioDeviceInfo> ListMicrophones()
    {
        using var e = new MMDeviceEnumerator();
        return e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName)).ToList();
    }
}
