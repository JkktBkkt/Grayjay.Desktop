import { Component } from 'solid-js';
import styles from './index.module.css';
import iconInfo from '../../../../assets/icons/icon_info_promo.svg';
import { useNavigate } from '@solidjs/router';

const BuyPromo: Component = () => {
    const navigate = useNavigate();
    return (
        <div class={styles.container}>
            <p class={styles.text}>
                No ads. No tracking. Just software you pay for.
            </p>
            <button class={styles.button} onClick={() => navigate('/web/buy')}>
                Buy a license
            </button>
        </div>
    );
};
export default BuyPromo;
