import { Component, createMemo, JSX, Show } from 'solid-js'

import styles from './index.module.css';
import { FocusableOptions } from '../../../nav';
import { focusable } from '../../../focusable'; void focusable;

interface ButtonProps {
    icon?: string;
    text: string;
    color?: string;
    hoverColor?: string;
    focusColor?: string;
    textColor?: string;
    hoverTextColor?: string;
    focusTextColor?: string;
    onClick?: (event: MouseEvent) => void;
    small?: boolean;
    style?: JSX.CSSProperties;
    focusableOpts?: FocusableOptions;
    autofocus?: boolean;
}

const Button: Component<ButtonProps> = (props) => {
    const handleClick = (event: MouseEvent) => {
        if (props.onClick) {
            props.onClick(event);
        }
    };

    const style = createMemo(() => {
        const bg = props.color ?? '#212122';
        const bgHover = props.hoverColor;
        const bgFocus = props.focusColor ?? '#fff';
        const text = props.textColor ?? '#fff';
        const textHover = props.hoverTextColor;
        const textFocus = props.focusTextColor ?? (props.focusColor ? text : '#141414');
        const iconFilterFocus = props.focusColor ? 'none' : 'brightness(0) saturate(100%)';

        const cssVars: Record<string, string> = {
            '--btn-bg': bg,
            '--btn-bg-focus': bgFocus,
            '--btn-text': text,
            '--btn-text-focus': textFocus,
            '--btn-icon-filter-focus': iconFilterFocus,
        };

        if (bgHover) cssVars['--btn-bg-hover'] = bgHover;
        if (textHover) cssVars['--btn-text-hover'] = textHover;

        return {
            ...props.style,
            ...cssVars,
            width: props.style?.width ?? 'fit-content',
        } as JSX.CSSProperties & Record<string, string>;
    });

    return (
        <div class={styles.container} classList={{[styles.small]: props.small}} style={style()} onClick={handleClick} use:focusable={props.focusableOpts} data-autofocus={props.autofocus ? '' : undefined}>
            <Show when={props.icon}>
                <img src={props.icon} class={styles.icon} alt={props.text} />
            </Show>
            <div class={styles.text}>{props.text}</div>
        </div>
    );
};

export default Button;