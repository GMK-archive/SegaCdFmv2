# Mega-CD FMV Deck

A .NET 10 MAUI Android app for importing a video, converting it into a compact
application-specific FMV image, and playing it through a retro-styled Sega
CD-inspired interface. The UI includes a SkiaSharp display, animated disc,
LCD counter, CRAM palette viewer, playback controls, and live video, color,
framerate, audio, and hardware-failure controls.

## What this app does (and does not do)

The importer uses Android's media codecs to sample video at 20 fps, reduce each
frame to a 320x224 RGB555 raster, and resample mixed audio to 32,552 Hz mono.
The app stores this data in its own compressed `.scdfmv` format in private app
storage. SkiaSharp draws frames with nearest-neighbor sampling. Playback
applies deterministic software models of the listed visual and audio quirks.

Video colors are mapped to the Genesis 9-bit master palette, with channel
levels `0, 36, 72, 109, 145, 182, 218, 255` (512 possible RGB colors). The
optional VDP color-limit quirk reduces each frame to four 16-color palettes.

This is a Sega CD-inspired software simulation, **not** a cycle-accurate Sega CD
emulator, a hardware-compatible disc authoring tool, or a genuine Cinepak /
TruVideo encoder. The RF5C164 behavior is approximated from already-mixed audio;
the original eight independent PCM voices and hardware timing are not present
in a decoded video file. Effects such as composite artifacts, VDP palettes,
CD bandwidth limits, and hardware failures are illustrative models, not
validated against original console hardware. The app currently targets Android;
the UI and core pipeline are MAUI/C# but media decoding and audio output use
Android platform APIs.

The importer caps a clip at 180 seconds and uses Android-supported source
formats/codecs. Conversion needs enough free storage for the temporary source,
the encoded image, and the decoded audio. Imported files are cached by source
content hash in private app data.

## Requirements

- .NET 10 SDK
- .NET MAUI Android workload
- Android SDK platform/build-tools supported by the installed .NET Android
  workload (API 36 for the current SDK manifests)
- A JDK supported by that workload

Install the workload if needed:

```sh
dotnet workload install maui-android
```

Install/configure the Android SDK and JDK using the
[.NET for Android setup guide](https://learn.microsoft.com/dotnet/android/getting-started/installation/).
Set `ANDROID_HOME` or `ANDROID_SDK_ROOT` if the SDK is not in the default path.

## Build and publish

Run these commands from the repository root:

```sh
dotnet restore SegaCdFmv/SegaCdFmv.csproj
dotnet build SegaCdFmv/SegaCdFmv.csproj -f net10.0-android
dotnet publish SegaCdFmv/SegaCdFmv.csproj -f net10.0-android -c Release
```

The project is configured for APK packaging. The publish output is normally
under:

```text
SegaCdFmv/bin/Release/net10.0-android/publish/
```

If the linker is terminated by a constrained build environment, publish without
trimming/AOT (the APK is larger):

```sh
dotnet publish SegaCdFmv/SegaCdFmv.csproj \
  -f net10.0-android -c Release \
  -p:PublishTrimmed=false \
  -p:AndroidLinkMode=None \
  -p:RunAOTCompilation=false
```

For a locally installable APK, Android builds are debug-signed by default in
Debug configuration:

```sh
dotnet build SegaCdFmv/SegaCdFmv.csproj -f net10.0-android -c Debug
```

For a release APK, provide a keystore and credentials through MSBuild properties
or a local, untracked `Directory.Build.props` file. Do not commit keystore
passwords:

```sh
dotnet publish SegaCdFmv/SegaCdFmv.csproj \
  -f net10.0-android -c Release \
  -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore=/absolute/path/to/release.keystore \
  -p:AndroidSigningKeyAlias=your-alias \
  -p:AndroidSigningKeyPass=your-key-password \
  -p:AndroidSigningStorePass=your-store-password
```

Release-signing options vary by Android SDK/workload version. Verify the
resulting APK signature before distribution and keep the signing key secure.