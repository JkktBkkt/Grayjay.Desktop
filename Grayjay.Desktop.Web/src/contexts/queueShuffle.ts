import { IPlatformVideo } from "../backend/models/content/IPlatformVideo";
import { shuffleArray } from "../utility";

/**
 * Returns a shuffled copy where the item at `index` stays in place and the items before and after it are shuffled separately.
 */
export function shuffleAroundIndex(videos: IPlatformVideo[], index: number): IPlatformVideo[] {
    const previousItems = shuffleArray(videos.slice(0, index));
    const nextItems = shuffleArray(videos.slice(index + 1));
    return [...previousItems, videos[index], ...nextItems];
}

/**
 * Returns a copy with `video` inserted at a random position after `index` (the end of the list included).
 */
export function insertRandomlyAfter(videos: IPlatformVideo[], index: number, video: IPlatformVideo): IPlatformVideo[] {
    const firstPosition = index + 1;
    const position = firstPosition + Math.floor(Math.random() * (videos.length - firstPosition + 1));
    return [...videos.slice(0, position), video, ...videos.slice(position)];
}
