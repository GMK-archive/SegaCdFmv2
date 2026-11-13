using SegaCdFmv.Core;
using SkiaSharp.Views.Maui.Controls.Hosting;
#if ANDROID
using SegaCdFmv.Droid;
#endif

namespace SegaCdFmv;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var b = MauiApp.CreateBuilder();
        b.UseMauiApp<App>().UseSkiaSharp();
#if ANDROID
        b.Services.AddSingleton<IMediaDecoder, AndroidMediaDecoder>();
        b.Services.AddSingleton<IAudioSink, AndroidAudioSink>();
#endif
        b.Services.AddSingleton<MainPage>();
        return b.Build();
    }
}
