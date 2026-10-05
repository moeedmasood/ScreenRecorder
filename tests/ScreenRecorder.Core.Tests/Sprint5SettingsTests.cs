using ScreenRecorder.Core;

namespace ScreenRecorder.Core.Tests;

public class Sprint5SettingsTests
{
    [Fact]
    public void Normalize_repairs_countdown_and_after_save()
    {
        var s = new AppSettings { CountdownSeconds = 7, AfterSave = (PostSaveAction)99 };
        s.Normalize();
        Assert.Equal(3, s.CountdownSeconds);
        Assert.Equal(PostSaveAction.None, s.AfterSave);
    }

    [Fact]
    public void Defaults_are_countdown_3_and_no_post_action()
    {
        var s = new AppSettings();
        Assert.Equal(3, s.CountdownSeconds);
        Assert.Equal(PostSaveAction.None, s.AfterSave);
    }
}
