import { Component, For, JSX, Show } from 'solid-js'

import styles from './index.module.css';

interface EmptyContentAction {
  icon?: string,
  title: string,
  action: () => void,
  color?: string
}

interface EmptyContentViewProps {
  style?: JSX.CSSProperties;
  icon: string,
  title: string,
  description: string,
  actions: EmptyContentAction[]
}

const EmptyContentView: Component<EmptyContentViewProps> = (props) => {

  return (
    <div class={styles.container} style={props.style}>
      <div class={styles.noSubs}>
        <div class={styles.icon}>
          <img src={props.icon} alt="" />
        </div>
        <div class={styles.title}>
          {props.title}
        </div>
        <div class={styles.description}>
          {props.description}
        </div>
        <div class={styles.buttons}>
          <For each={props.actions}>{(btn: EmptyContentAction) =>
            <button
              class={styles.primaryButton}
              onClick={() => btn.action()}
            >
              <Show when={btn.icon}>
                <img src={btn.icon} alt="" class={styles.buttonIcon} />
              </Show>
              <span>{btn.title}</span>
            </button>
          }</For>
        </div>
      </div>
    </div>
  );
};

export default EmptyContentView;