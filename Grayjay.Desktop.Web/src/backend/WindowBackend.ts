import Globals from "../globals";
import { Backend } from "./Backend";
import { IPlatformVideo } from "./models/content/IPlatformVideo";

export interface IOrderedPlatformVideo extends IPlatformVideo {
    index: number;
}

export abstract class WindowBackend {
    static async startWindow(): Promise<Boolean> {
        return await Backend.GET("/window/startWindow")
    }

    static async ready(): Promise<boolean> {
        return await Backend.GET("/window/Ready");
    }

    // A beacon cannot set headers, so the window id goes in the query.
    static unload(): void {
        navigator.sendBeacon("/window/Unload?windowId=" + encodeURIComponent(Globals.WindowID));
    }

    static async delay(ms: number): Promise<boolean> {
        return await Backend.GET("/window/Delay?ms=" + ms);
    }

    static async echo(str: string): Promise<boolean> {
        await Backend.GET("/window/Echo?str=" + str);
        return true;
    }

    static async closeWindow(): Promise<boolean> {
        await Backend.GET("/window/Close");
        return true;
    }

    static async inputSource(): Promise<string | undefined> {
        const inputSource = await Backend.GET_text("/window/InputSource");
        let s: string | undefined | null = inputSource;
        if (s && s.length)
            return s;
        return undefined;
    }
}