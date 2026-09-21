# GtV + Dynamic Library: Set Your Language

GtV's streaming playback uses Dynamic Library for virtual catalog items, AIOStreams source selection, and selected-source probing.

## Important: set your preferred audio language

Before using GtV for multi-audio streaming:

1. Open **Jellyfin Dashboard**.
2. Go to **Plugins > Dynamic Library**.
3. Set the preferred language/audio language to the language you want, for example **English**.
4. Save.
5. Restart Jellyfin if prompted.

## Why

AIOStreams sources may contain multiple embedded audio tracks. For example:

- Polish + English
- Italian + English
- Spanish + English

The container may mark a non-English stream as its default track. In current GtV testing, Dynamic Library's configured language preference reliably determines the audio language used at playback start when that language exists in the selected source.

Example:

```text
Preferred Language: English
```

With English available in the selected source, playback starts in English even when the source container marks Polish or Italian as default.

If the preferred language is unavailable, playback falls back to an available track.

## Tested GtV integration state

The current tested GtV/Dynamic Library integration includes:

- AIOStreams source/version picker available before first playback.
- Focused-episode enrichment only, avoiding season-wide source lookups.
- Selected-source-only ffprobe probing.
- Real MediaStreams including language, codec, default and forced flags.
- Existing Jellyfin/HDHomeRun behavior left untouched.

Manual in-player audio switching is still being investigated. Until that is resolved, configure the preferred language in Dynamic Library before playback.

## Server note

Do not remove or falsify ffprobe `Default` / `Forced` disposition metadata. The preferred language setting should be used to choose the desired playback language while keeping the source metadata truthful.
