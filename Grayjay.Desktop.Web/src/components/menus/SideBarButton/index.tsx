import { Show, type Component, createSignal } from 'solid-js';

import styles from './index.module.css';
import type { FocusableOptions } from "../../../nav";
import { focusable } from "../../../focusable"; void focusable;

interface SideBarButtonProps {
  icon?: string;
  iconHover?: string;
  name: string;
  subtitle?: string;
  selected?: boolean;
  collapsed?: boolean;
  highlight?: boolean;
  onClick?: (event: MouseEvent) => void;
  onRightClick?: (event: MouseEvent) => void;
  focusableOpts?: FocusableOptions;
  onFocus?: () => void;
  onBlur?: () => void;
}

const SideBarButton: Component<SideBarButtonProps> = (props) => {
  const [isHovered, setIsHovered] = createSignal(false);

  const handleClick = (event: MouseEvent) => {
    if (props.onClick) {
      props.onClick(event);
    }
  };
  const handleRightClick = (event: MouseEvent) => {
    if (props.onRightClick) {
      props.onRightClick(event);
    }
  };

  const currentIcon = () => {
    if (props.iconHover && (isHovered() || props.selected)) {
      return props.iconHover;
    }
    return props.icon;
  };

  return (
    <div
      use:focusable={props.focusableOpts}
      onClick={handleClick}
      onContextMenu={handleRightClick}
      onMouseEnter={() => setIsHovered(true)}
      onMouseLeave={() => setIsHovered(false)}
      class={styles.sideBarButton}
      classList={{ [styles.selected]: props.selected, [styles.collapsed]: props.collapsed, [styles.highlight]: props.highlight }}
      onFocus={() => {
        console.info("sidebarbutton onFocus");
        props.onFocus?.();
      }}
      onBlur={props.onBlur}
    >
      <Show when={currentIcon()}>
        <img src={currentIcon()} class={styles.icon} alt="logo" />
      </Show>
      <div class={styles.textContainer}>
        <div class={styles.text}>{props.name}</div>
        <Show when={props.subtitle && !props.collapsed}>
          <div class={styles.subtitle}>{props.subtitle}</div>
        </Show>
      </div>
    </div>
  );
};

export default SideBarButton;

