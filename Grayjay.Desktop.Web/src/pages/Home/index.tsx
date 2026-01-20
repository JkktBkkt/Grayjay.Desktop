import { createResource, type Component, Show, onMount } from 'solid-js';

import styles from './index.module.css';
import { HomeBackend } from '../../backend/HomeBackend';
import ContentGrid from '../../components/containers/ContentGrid';
import NavigationBar from '../../components/topbars/NavigationBar';
import ScrollContainer from '../../components/containers/ScrollContainer';
import StateGlobal from '../../state/StateGlobal';
import { DateTime } from 'luxon';
import IconButton from '../../components/buttons/IconButton';
import NavIconButton from '../../components/buttons/NavIconButton';

import iconRefresh from "../../assets/icons/icon_reload_temp.svg"
import iconWarning from "../../assets/icons/icon_warning_triangle.svg"
import iconSourcesSmall from "../../assets/icons/icon_sources_small.svg"
import { useNavigate } from '@solidjs/router';
import EmptyContentView from '../../components/EmptyContentView';
import { focusable } from '../../focusable'; void focusable;
import LiveChatWindow from '../../components/LiveChatWindow';
import UIOverlay from '../../state/UIOverlay';

const HomePage: Component = () => {
  const homePager = StateGlobal.home$;

  const nav = useNavigate();

  //createResource(async () => await HomeBackend.homePagerLazy());
  const lastHomeMillis = Math.abs(StateGlobal.lastHomeTime$()?.diffNow().toMillis());
  if ((lastHomeMillis ?? 0) > 2 * 60 * 1000) {
    StateGlobal.reloadHome();
  }
  console.log("Home page with resource: ", homePager());
  console.log("Home page with resource state: ", homePager.state);

  let scrollContainerRef: HTMLDivElement | undefined;
  return (
    <div class={styles.container}>
      <NavigationBar isRoot={true} childrenAfter={
        <NavIconButton
          icon={iconRefresh}
          onClick={() => { StateGlobal.reloadHome() }}
          focusableOpts={{
            groupId: 'nav-bar',
            groupIndices: [1],
            groupType: 'horizontal',
            onPress: () => StateGlobal.reloadHome()
          }} />
      } />
      <Show when={homePager.state == 'ready'}>
        <Show when={homePager() && homePager()!.data.length > 0}>
          <ScrollContainer ref={scrollContainerRef}>
            <ContentGrid pager={homePager()} outerContainerRef={scrollContainerRef} openChannelButton={true} />
          </ScrollContainer>
        </Show>
        <Show when={homePager() && homePager()!.data.length == 0}>
          <EmptyContentView icon={iconWarning} title='No sources enabled' description='To start watching videos, enable at least one source (YouTube, PeerTube, etc.)' actions={[
            {
              icon: iconSourcesSmall,
              title: "Enable sources",
              action: () => nav("/web/sources")
            }
          ]} />
        </Show>
      </Show>
    </div>
  );
};

export default HomePage;
