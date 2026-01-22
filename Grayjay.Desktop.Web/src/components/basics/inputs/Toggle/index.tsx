import { Component, createEffect, createSignal } from 'solid-js'

import styles from './index.module.css';
import { FocusableOptions } from '../../../../nav';
import { focusable } from '../../../../focusable'; void focusable;

interface ToggleProps {
    value: boolean;
    onToggle: (value: boolean) => void;
    adjustInternally?: boolean;
    focusableOpts?: FocusableOptions;
}

const Toggle: Component<ToggleProps> = (props) => {
    const adjustInternally = props.adjustInternally ?? true;
    const [toggle, setToggle] = createSignal(props.value);
    createEffect(() => {
        setToggle(props.value);
    });

    function handleToggle(ev: MouseEvent) {
        const newValue = !toggle();
        if (adjustInternally)
            setToggle(newValue);
        props.onToggle(newValue);
        ev.preventDefault();
        ev.stopPropagation();
    }

    return (
        <div class={styles.toggle} classList={{ [styles.enabled]: toggle() }} onClick={(ev) => handleToggle(ev)} use:focusable={props.focusableOpts}>
            <div class={styles.thumb}></div>
        </div>
    );
};

export default Toggle;