import { Component, JSX, Show } from 'solid-js'

import styles from './index.module.css';
import Button from '../Button';
import { FocusableOptions } from '../../../nav';

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
}

const ButtonFlex: Component<ButtonProps> = (props) => {
    const style = props.style ?? {};
    style.display = "flex";
    style["align-items"] = "center";
    style["flex-direction"] = "row";
    style["justify-content"] = "center";

    return (
        <Button
            icon={props.icon}
            text={props.text}
            color={props.color}
            hoverColor={props.hoverColor}
            focusColor={props.focusColor}
            textColor={props.textColor}
            hoverTextColor={props.hoverTextColor}
            focusTextColor={props.focusTextColor}
            onClick={props.onClick}
            small={props.small}
            style={style}
            focusableOpts={props.focusableOpts}
        ></Button>
    );
};

export default ButtonFlex;