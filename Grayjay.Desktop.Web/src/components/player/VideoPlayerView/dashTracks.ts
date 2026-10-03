import type { MediaInfo, MediaPlayerClass } from "dashjs";

const DASH_ROLE_SCHEME_ID = "urn:mpeg:dash:role:2011";
const AUDIO_PURPOSE_SCHEME_ID = "urn:tva:metadata:cs:AudioPurposeCS:2007";
// DVB audio purpose codes used on audio Accessibility descriptors.
const AUDIO_PURPOSE_NAMES: Record<string, string> = { "1": "audio description", "2": "hearing impaired" };
const ACCESSIBILITY_FALLBACK_NAME = "accessibility";
const MAIN_ROLE = "main";
// Roles that carry the programme itself; any other role (commentary, description, ...) marks a variant.
const PROGRAMME_AUDIO_ROLES = [MAIN_ROLE, "dub", "alternate"];
// Some manifests mark audio description and dialogue boost only in the adaptation's audioTrackId attribute.
const VARIANT_AUDIO_TRACK_ID_MARKERS = ["descriptive", "boosteddialog"];
const FALLBACK_AUDIO_LANGUAGE = "en";
// Manifests usually label tracks in English, whatever the UI language is.
const LABEL_LANGUAGE_NAME_LOCALE = "en";
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

export interface DashTrackSelection {
    representations: DashVideoRepresentation[];
    audioTracks: DashAudioTrack[];
    // Undefined means automatic quality.
    representationId?: string;
    audioTrackKey?: string;
}

type BootstrapAwareMediaInfo = MediaInfo & { segmentSequenceProperties?: { isBootstrapConfiguration(): boolean }[] };

export function primarySubtag(language: string): string {
    return language.split(/[-_]/)[0].toLowerCase();
}

function dashRoleValues(mediaInfo: MediaInfo): string[] {
    return (mediaInfo.roles ?? []).filter(role => role.schemeIdUri === DASH_ROLE_SCHEME_ID).map(role => role.value ?? "");
}

function accessibilitySignature(mediaInfo: MediaInfo): string {
    return (mediaInfo.accessibility ?? []).map(descriptor => `${descriptor.schemeIdUri}=${descriptor.value ?? ""}`).sort().join(",");
}

function accessibilityName(mediaInfo: MediaInfo): string {
    const descriptor = mediaInfo.accessibility?.[0];
    if (!descriptor) {
        return "";
    }
    if (descriptor.schemeIdUri === DASH_ROLE_SCHEME_ID && descriptor.value) {
        return descriptor.value;
    }
    if (descriptor.schemeIdUri === AUDIO_PURPOSE_SCHEME_ID) {
        return AUDIO_PURPOSE_NAMES[descriptor.value ?? ""] ?? ACCESSIBILITY_FALLBACK_NAME;
    }
    return ACCESSIBILITY_FALLBACK_NAME;
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
    // An explicit region in lang wins, and a bare prefix such as "en" adds nothing.
    if (/[-_]/.test(language) || !/[-_]/.test(idLanguage)) {
        return language;
    }
    try {
        return Intl.getCanonicalLocales(idLanguage)[0] ?? language;
    } catch (e) {
        return language;
    }
}

export function describeAudioTrack(player: MediaPlayerClass, mediaInfo: MediaInfo): DashAudioTrack {
    const language = mediaInfo.lang ?? "";
    const label = mediaInfo.labels?.find(entry => !!entry.text)?.text ?? "";
    const audioTrackId = readAudioTrackId(player, mediaInfo);
    const roleValues = dashRoleValues(mediaInfo);
    const role = roleValues.find(value => value === MAIN_ROLE) ?? roleValues[0] ?? "";
    const variantMarker = VARIANT_AUDIO_TRACK_ID_MARKERS.find(marker => audioTrackId.includes(marker)) ?? "";
    const isVariant = roleValues.some(value => !PROGRAMME_AUDIO_ROLES.includes(value))
        || (mediaInfo.accessibility?.length ?? 0) > 0
        || !!variantMarker;
    const maxBandwidth = Math.max(0, ...mediaInfo.bitrateList.map(bitrate => bitrate.bandwidth ?? 0));
    const displayLanguage = regionalLanguage(language, audioTrackId);

    return {
        // Region and variant marker keep pt-BR/pt-PT and descriptive audio apart when they share a label.
        key: [displayLanguage, label || audioTrackId, variantMarker, role, accessibilitySignature(mediaInfo)].join("|"),
        language,
        displayLanguage,
        label,
        audioTrackId,
        role,
        isVariant,
        maxBandwidth,
        codec: mediaInfo.codec ?? "",
        mediaInfo
    };
}

// The display language keeps its region only when the same language is offered in more than one region.
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

function isLanguageNameLabel(track: DashAudioTrack): boolean {
    if (!track.label || !track.language) {
        return false;
    }
    const normalizedLabel = track.label.trim().toLowerCase();
    const languageCodes = [primarySubtag(track.language), track.displayLanguage].filter(code => !!code);
    return [navigator.language, LABEL_LANGUAGE_NAME_LOCALE].some(locale => languageCodes.some(code => {
        try {
            return new Intl.DisplayNames([locale], { type: "language" }).of(code)?.toLowerCase() === normalizedLabel;
        } catch (e) {
            return false;
        }
    }));
}

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
    const label = isLanguageNameLabel(track) ? "" : track.label;
    const detail = label || idParts.join(" ") || (track.role && track.role !== MAIN_ROLE ? track.role : "") || accessibilityName(track.mediaInfo);
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

// The dash.js 5.0.3 default pick, as a custom selection function replaces it for every track type.
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
