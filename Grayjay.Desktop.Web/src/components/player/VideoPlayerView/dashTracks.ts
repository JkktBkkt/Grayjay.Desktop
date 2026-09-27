import type { MediaInfo, MediaPlayerClass } from "dashjs";

const DASH_ROLE_SCHEME_ID = "urn:mpeg:dash:role:2011";
const MAIN_ROLE = "main";
// Roles that carry the programme itself; any other role (commentary, description, ...) marks a variant.
const PROGRAMME_AUDIO_ROLES = [MAIN_ROLE, "dub", "alternate"];
// Some manifests mark audio description and dialogue boost only in the adaptation's audioTrackId attribute.
const VARIANT_AUDIO_TRACK_ID_MARKERS = ["descriptive", "boosteddialog"];
const FALLBACK_AUDIO_LANGUAGE = "en";
const AUDIO_TRACK_ID_INDEX_REGEX = /^\d+$/;

export interface DashVideoRepresentation {
    id: string;
    width: number;
    height: number;
    bandwidth: number;
}

export interface DashAudioTrack {
    key: string;
    language: string;
    displayLanguage: string;
    label: string;
    audioTrackId: string;
    role: string;
    isVariant: boolean;
    maxBandwidth: number;
    codec: string;
    mediaInfo: MediaInfo;
}

type BootstrapAwareMediaInfo = MediaInfo & { segmentSequenceProperties?: { isBootstrapConfiguration(): boolean }[] };

export function primarySubtag(language: string): string {
    return language.split(/[-_]/)[0].toLowerCase();
}

function dashRoleValues(mediaInfo: MediaInfo): string[] {
    return (mediaInfo.roles ?? []).filter(role => role.schemeIdUri === DASH_ROLE_SCHEME_ID).map(role => role.value);
}

function readAudioTrackId(player: MediaPlayerClass, mediaInfo: MediaInfo): string {
    if (!mediaInfo.streamInfo) {
        return "";
    }
    try {
        // dash.js keeps unknown AdaptationSet attributes on the raw adaptation but does not expose them on MediaInfo.
        const adaptation = player.getDashAdapter().getRealAdaptation(mediaInfo.streamInfo, mediaInfo) as { audioTrackId?: unknown } | null;
        return typeof adaptation?.audioTrackId === "string" ? adaptation.audioTrackId : "";
    } catch (e) {
        console.warn("Failed to read the DASH audio track id", e);
        return "";
    }
}

function regionalLanguage(language: string, audioTrackId: string): string {
    // Some manifests set lang="pt" on both pt-BR and pt-PT; only the audioTrackId prefix carries the region.
    const idLanguage = audioTrackId.split("_")[0];
    if (!language || !idLanguage || primarySubtag(idLanguage) !== primarySubtag(language)) {
        return language;
    }
    try {
        return Intl.getCanonicalLocales(idLanguage)[0] ?? language;
    } catch (e) {
        return language;
    }
}

/**
 * Describes one dash.js audio track. Copies of one track in different codecs share the same key.
 */
export function describeAudioTrack(player: MediaPlayerClass, mediaInfo: MediaInfo): DashAudioTrack {
    const language = mediaInfo.lang ?? "";
    const label = mediaInfo.labels?.find(entry => !!entry.text)?.text ?? "";
    const audioTrackId = readAudioTrackId(player, mediaInfo);
    const roleValues = dashRoleValues(mediaInfo);
    const role = roleValues.find(value => value === MAIN_ROLE) ?? roleValues[0] ?? "";
    const isVariant = roleValues.some(value => !PROGRAMME_AUDIO_ROLES.includes(value))
        || (mediaInfo.accessibility?.length ?? 0) > 0
        || VARIANT_AUDIO_TRACK_ID_MARKERS.some(marker => audioTrackId.includes(marker));
    const maxBandwidth = Math.max(0, ...mediaInfo.bitrateList.map(bitrate => bitrate.bandwidth ?? 0));

    return {
        key: [language, label || audioTrackId, role].join("|"),
        language,
        displayLanguage: regionalLanguage(language, audioTrackId),
        label,
        audioTrackId,
        role,
        isVariant,
        maxBandwidth,
        codec: mediaInfo.codec ?? "",
        mediaInfo
    };
}

/**
 * Returns one entry per track key, keeping the copy with the highest bandwidth.
 * The display language keeps its region only when the same language is offered in more than one region.
 */
export function groupAudioTracks(tracks: DashAudioTrack[]): DashAudioTrack[] {
    const groups = new Map<string, DashAudioTrack>();
    for (const track of tracks) {
        const current = groups.get(track.key);
        if (!current || track.maxBandwidth > current.maxBandwidth) {
            groups.set(track.key, track);
        }
    }

    const regionsByLanguage = new Map<string, Set<string>>();
    for (const track of groups.values()) {
        const regions = regionsByLanguage.get(track.language) ?? new Set<string>();
        regions.add(track.displayLanguage);
        regionsByLanguage.set(track.language, regions);
    }
    return [...groups.values()].map(track => {
        if ((regionsByLanguage.get(track.language)?.size ?? 0) > 1) {
            return track;
        }
        return { ...track, displayLanguage: track.language };
    });
}

/**
 * Picks the audio track playback starts on, from the Primary Language and Prefer Original Audio settings.
 */
export function pickInitialAudioTrack(tracks: DashAudioTrack[], preferredLanguage: string | null | undefined, preferOriginal: boolean | undefined): DashAudioTrack | undefined {
    if (tracks.length === 0) {
        return undefined;
    }

    let candidates = tracks;
    if (preferOriginal) {
        const mainTracks = candidates.filter(track => track.role === MAIN_ROLE);
        if (mainTracks.length > 0 && mainTracks.length < candidates.length) {
            candidates = mainTracks;
        }
    }

    const programmeTracks = candidates.filter(track => !track.isVariant);
    if (programmeTracks.length > 0) {
        candidates = programmeTracks;
    }

    const firstLanguage = candidates[0].language;
    const languages = [preferredLanguage, navigator.language, FALLBACK_AUDIO_LANGUAGE, firstLanguage];
    for (const language of languages) {
        if (!language) {
            continue;
        }
        const matches = candidates.filter(track => primarySubtag(track.language) === primarySubtag(language));
        if (matches.length > 0) {
            candidates = matches;
            break;
        }
    }

    return candidates.reduce((best, track) => track.maxBandwidth > best.maxBandwidth ? track : best);
}

/**
 * Display name of a grouped audio track, for example "English (dialog)".
 */
export function formatAudioTrackName(track: DashAudioTrack): string {
    let languageName = track.displayLanguage;
    if (track.displayLanguage) {
        try {
            languageName = new Intl.DisplayNames([navigator.language], { type: "language" }).of(track.displayLanguage) ?? track.displayLanguage;
        } catch (e) {
            languageName = track.displayLanguage;
        }
    }

    // Some audioTrackIds read "<language>_<name>_<index>", for example "en-us_boosteddialoghigh_0".
    const idParts = track.audioTrackId.split("_").slice(1).filter(part => !AUDIO_TRACK_ID_INDEX_REGEX.test(part));
    const detail = track.label || idParts.join(" ") || (track.role && track.role !== MAIN_ROLE ? track.role : "");
    const name = detail ? `${languageName} (${detail})` : languageName;
    return name || "Unknown";
}

function tracksWithHighestSelectionPriority(tracks: MediaInfo[]): MediaInfo[] {
    let highest = 0;
    let result: MediaInfo[] = [];
    for (const track of tracks) {
        if (isNaN(track.selectionPriority)) {
            continue;
        }
        if (track.selectionPriority > highest) {
            highest = track.selectionPriority;
            result = [track];
        } else if (track.selectionPriority === highest) {
            result.push(track);
        }
    }
    return result;
}

function tracksWithMainRole(tracks: MediaInfo[]): MediaInfo[] {
    const mainTracks = tracks.filter(track => dashRoleValues(track).includes(MAIN_ROLE));
    if (mainTracks.length > 0) {
        return mainTracks;
    }
    const rolelessTracks = tracks.filter(track => !track.roles || track.roles.length === 0);
    return rolelessTracks.length > 0 ? rolelessTracks : tracks;
}

function tracksWithHighestVideoEfficiency(tracks: MediaInfo[]): MediaInfo[] {
    let lowest = Infinity;
    let result: MediaInfo[] = [];
    for (const track of tracks) {
        const bitsPerPixel = track.bitrateList.reduce((sum, bitrate) =>
            sum + Number(bitrate.bandwidth) / Math.max(1, Number(bitrate.width) * Number(bitrate.height)), 0) / track.bitrateList.length;
        if (bitsPerPixel < lowest) {
            lowest = bitsPerPixel;
            result = [track];
        } else if (bitsPerPixel === lowest) {
            result.push(track);
        }
    }
    return result;
}

function tracksWithHighestBitrate(tracks: MediaInfo[]): MediaInfo[] {
    let highest = 0;
    let result: MediaInfo[] = [];
    for (const track of tracks) {
        const bandwidth = Math.max(...track.bitrateList.map(bitrate => Number(bitrate.bandwidth)));
        if (bandwidth > highest) {
            highest = bandwidth;
            result = [track];
        } else if (bandwidth === highest) {
            result.push(track);
        }
    }
    return result;
}

/**
 * The dash.js 5.2 default initial pick (selection priority, main role, lowest startup delay) with default settings.
 * A custom selection function replaces that logic for every type, so non-audio types are routed back through this.
 * Not reproduced: the closest-bitrate pick dash.js makes when a later period starts.
 */
export function pickDefaultTracks(tracks: MediaInfo[]): MediaInfo[] {
    let candidates = tracksWithHighestSelectionPriority(tracks);
    if (candidates.length > 1) {
        candidates = tracksWithMainRole(candidates);
    }
    if (candidates.length > 1) {
        const bootstrapTracks = candidates.filter(track =>
            (track as BootstrapAwareMediaInfo).segmentSequenceProperties?.some(properties => properties.isBootstrapConfiguration()) ?? false);
        if (bootstrapTracks.length > 0) {
            candidates = bootstrapTracks;
        }
        if (candidates[0]?.type === "video") {
            candidates = tracksWithHighestVideoEfficiency(candidates);
        }
        if (candidates.length > 1) {
            candidates = tracksWithHighestBitrate(candidates);
        }
    }
    return candidates;
}
