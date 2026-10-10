using SSGen.Core;
using SSGen.Core.Setup;
using SSGen.Emulator.Avd;
using SSGen.Emulator.Devices;
using SSGen.Emulator.Mirror;

namespace SSGen.Emulator.Tests;

public class AvdManagerTests
{
    private static (AvdManager Manager, SystemImage Image, string SdkRoot) Setup(TempDir dir)
    {
        var sdk = dir.Combine("sdk");
        var imageDir = dir.Combine("sdk", "system-images", "android-36", "google_apis_playstore", "x86_64");
        Directory.CreateDirectory(imageDir);
        return (new AvdManager(dir.Combine("avd")), new SystemImage("android-36", "google_apis_playstore", "x86_64", imageDir), sdk);
    }

    [Fact]
    public void Creates_lists_and_deletes_managed_avd()
    {
        using var dir = new TempDir();
        var (manager, image, sdk) = Setup(dir);

        var name = manager.SuggestName(DeviceProfile.PixelPhone, image);
        Assert.Equal("SSGen_Pixel_API36", name);
        var avd = manager.Create(name, DeviceProfile.PixelPhone, image, sdk);

        Assert.True(avd.IsManagedBySSGen);
        Assert.True(avd.IsComplete);
        Assert.Equal(36, avd.ApiLevel);
        Assert.Equal((1080, 2400), (avd.Width, avd.Height));
        var config = File.ReadAllText(Path.Combine(avd.Directory!, "config.ini"));
        Assert.Contains(@"image.sysdir.1=system-images\android-36\google_apis_playstore\x86_64\", config, StringComparison.Ordinal);
        Assert.Contains("PlayStore.enabled=true", config, StringComparison.Ordinal);
        Assert.Equal("SSGen_Pixel_API36_2", manager.SuggestName(DeviceProfile.PixelPhone, image));

        AvdManager.Delete(avd);
        Assert.Empty(manager.List());
    }

    [Fact]
    public void Refuses_duplicates_bad_names_and_unmanaged_deletes()
    {
        using var dir = new TempDir();
        var (manager, image, sdk) = Setup(dir);
        manager.Create("A", DeviceProfile.SmallPhone, image, sdk);

        Assert.Throws<UserFacingException>(() => manager.Create("A", DeviceProfile.SmallPhone, image, sdk));
        Assert.Throws<UserFacingException>(() => manager.Create("bad name!", DeviceProfile.SmallPhone, image, sdk));

        // An AVD the user made in Android Studio (no SSGen marker), whose folder name differs from the ini name.
        Directory.CreateDirectory(dir.Combine("avd", "MADI_2.avd"));
        File.WriteAllText(dir.Combine("avd", "MADI_2.avd", "config.ini"), "AvdId=MaDi\nhw.lcd.width=1280\nhw.lcd.height=2856\n");
        File.WriteAllText(dir.Combine("avd", "MADI.ini"), $"path={dir.Combine("avd", "MADI_2.avd")}\ntarget=android-36.1\n");
        var madi = manager.Find("MADI")!;
        Assert.False(madi.IsManagedBySSGen);
        Assert.Equal(36, madi.ApiLevel);
        Assert.Throws<UserFacingException>(() => AvdManager.Delete(madi));

        // AVDs created before the rename carry the legacy marker and must stay managed.
        Directory.CreateDirectory(dir.Combine("avd", "Old.avd"));
        File.WriteAllText(dir.Combine("avd", "Old.avd", "config.ini"), "storeshot.managed=yes\n");
        File.WriteAllText(dir.Combine("avd", "Old.ini"), $"path={dir.Combine("avd", "Old.avd")}\n");
        Assert.True(manager.Find("Old")!.IsManagedBySSGen);
        Assert.True(Directory.Exists(dir.Combine("avd", "MADI_2.avd")));
    }
}

public class MockDeviceTests
{
    [Fact]
    public async Task Navigates_records_actions_and_renders()
    {
        var device = new MockDevice();
        Assert.Equal("home", device.CurrentScreen.Id);

        var workouts = device.CurrentScreen.Buttons.First(b => b.Label == "Workouts").Bounds;
        await device.TapAsync(workouts.MidX, workouts.MidY);
        Assert.Equal("workouts", device.CurrentScreen.Id);
        Assert.Equal("com.example.mock/.WorkoutsActivity", await device.GetForegroundActivityAsync());

        await device.PressKeyAsync(AndroidKey.Back);
        Assert.Equal("home", device.CurrentScreen.Id);
        Assert.Equal(["tap 540 1000", "key Back"], device.Actions);

        var xml = await device.DumpUiHierarchyAsync();
        Assert.Contains("text=\"Workouts\"", xml, StringComparison.Ordinal);
        Assert.Contains("bounds=\"[140,900][940,1100]\"", xml, StringComparison.Ordinal);

        using var png = SkiaSharp.SKBitmap.Decode(await device.CaptureScreenshotPngAsync());
        Assert.Equal((MockDevice.Width, MockDevice.Height), (png.Width, png.Height));
    }

    [Fact]
    public async Task Screencap_mirror_publishes_frames_and_maps_input()
    {
        var device = new MockDevice();
        await using var mirror = new ScreencapMirror(device, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await mirror.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (mirror.Frames.Version == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(MockDevice.Width, mirror.Frames.Width);
        var progress = device.CurrentScreen.Buttons.First(b => b.Label == "Progress").Bounds;
        await mirror.TouchAsync(TouchAction.Down, progress.MidX, progress.MidY);
        await mirror.TouchAsync(TouchAction.Up, progress.MidX + 3, progress.MidY);
        Assert.Equal("progress", device.CurrentScreen.Id);

        await mirror.TouchAsync(TouchAction.Down, 500, 1800);
        await mirror.TouchAsync(TouchAction.Up, 500, 600);
        Assert.StartsWith("swipe 500 1800 500 600", device.Actions.Last(), StringComparison.Ordinal);
        Assert.NotNull(mirror.Frames.SnapshotPng());
    }
}
