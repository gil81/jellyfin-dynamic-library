# GtV Dynamic Library 1.2.5 Notes

This branch is the GtV Dynamic Library integration baseline.

Current tested server behavior:

- Virtual TMDB/TVDB catalog remains enabled.
- AIOStreams source discovery remains lightweight.
- Only the selected source is ffprobed for real streams.
- PlaybackInfo returns real audio/video/subtitle metadata for the selected source.
- GtV home feeds remain available at:
  - `/DynamicLibrary/Home/NewReleaseMovies`
  - `/DynamicLibrary/Home/NewReleaseTV`
- Native Jellyfin items and HDHomeRun Live TV are not intended to be intercepted.

For multi-audio sources, configure the plugin's preferred language. See [SET-YOUR-LANGUAGE.md](SET-YOUR-LANGUAGE.md).

The tested runtime package reports assembly version `1.2.5.0` while the installed Jellyfin plugin package/display metadata may still show `1.5.0.0`. Treat the assembly/runtime behavior as the tested GtV 1.2.5 baseline.
