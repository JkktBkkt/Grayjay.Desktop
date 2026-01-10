import { Component, JSX } from 'solid-js';
import styles from './index.module.css';
import { focusable } from '../../../focusable';
import { FocusableOptions } from '../../../nav';
void focusable;

interface NavIconButtonProps {
    icon: string;
    onClick?: (e: MouseEvent) => void;
    style?: JSX.CSSProperties;
    imgStyle?: JSX.CSSProperties;
    focusableOpts?: FocusableOptions;
}

const NavIconButton: Component<NavIconButtonProps> = (props) => {
    return (
        <div class={styles.button}
            onClick={(e) => props.onClick?.(e)}
            style={props.style}
            use:focusable={props.focusableOpts}>
            <img src={props.icon} style={props.imgStyle} alt="" />
        </div>
    );
};

export default NavIconButton;
