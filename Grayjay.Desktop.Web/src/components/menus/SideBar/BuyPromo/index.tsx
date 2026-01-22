import { Component } from 'solid-js';
import styles from './index.module.css';
import iconInfo from '../../../../assets/icons/icon_info_promo.svg';
import { useNavigate } from '@solidjs/router';
import { focusable } from '../../../../focusable';
import { FocusableOptions } from '../../../../nav';
void focusable;

interface BuyPromoProps {
    focusableOpts?: FocusableOptions;
    onFocus?: () => void;
    onBlur?: () => void;
}

const BuyPromo: Component<BuyPromoProps> = (props) => {
    const navigate = useNavigate();
    return (
        <div class={styles.container}>
            <p class={styles.text}>
                No ads. No tracking. Just software you pay for.
            </p>
            <button 
                class={styles.button} 
                onClick={() => navigate('/web/buy')}
                use:focusable={props.focusableOpts}
                onFocus={props.onFocus}
                onBlur={props.onBlur}
            >
                Buy a license
            </button>
        </div>
    );
};
export default BuyPromo;
