<script setup lang="ts">
const props = defineProps<{
    view: 'Sources' | 'Curation' | 'Gallery' | 'Delivery';
    motionEnabled: boolean;
    hidden: boolean;
    continuous?: boolean;
}>();
const ringRotation = computed(() => props.motionEnabled ? ({ Sources: 0, Curation: 1.5, Gallery: 3, Delivery: 4.5 })[props.view] : 0);
const duration = computed(() => props.motionEnabled && !props.hidden ? '6000ms' : '0ms');
const ringAnimationState = computed(() => props.motionEnabled && !props.hidden ? 'running' : 'paused');
</script>

<template>
    <div v-show="!hidden" class="aperture-background" :style="{ '--aperture-duration': duration }" aria-hidden="true">
        <div class="aperture-lens">
<svg class="aperture-iris" viewBox="-1100 -1100 2200 2200" focusable="false">
    <defs>
        <clipPath id="aperture-visible-face">
            <path class="aperture-neighbor-clip" style="transform:translate(906.31px, 422.62px) rotate(-9.4deg) translate(-906.31px, -422.62px) rotate(60deg) translate(906.31px, 422.62px) rotate(9.4deg) translate(-906.31px, -422.62px);" d="M -3000 -3000 H 3000 V 3000 H -3000 Z M 260 -103.923 L -40 415.692 C 90 500 160 710 20.94 1199.82 A 1200 1200 0 0 0 1087.57 507.14 L 906.31 422.62 C 650 180 400 -50 260 -103.923 Z" clip-rule="evenodd" />
        </clipPath>
        <clipPath id="aperture-outside-face">
            <path d="M -3000 -3000 H 3000 V 3000 H -3000 Z M 260 -103.923 L -40 415.692 C 90 500 160 710 20.94 1199.82 A 1200 1200 0 0 0 1087.57 507.14 L 906.31 422.62 C 650 180 400 -50 260 -103.923 Z" clip-rule="evenodd" />
        </clipPath>
        <clipPath id="aperture-barrel">
            <circle r="1000" />
        </clipPath>
    </defs>
    <g clip-path="url(#aperture-barrel)">
        <g class="aperture-blades">
            <g transform="rotate(0)">
                <g class="aperture-leaf" style="transform:rotate(9.4deg);" clip-path="url(#aperture-visible-face)">
                    <path class="aperture-blade" style="fill:#0c0c0c" d="M 260 -103.923 L -40 415.692 C 90 500 160 710 20.94 1199.82 A 1200 1200 0 0 0 1087.57 507.14 L 906.31 422.62 C 650 180 400 -50 260 -103.923 Z" />
                    <path class="aperture-rim" d="M -40 415.692 L 260 -103.923 C 400 -50 650 180 906.31 422.62" />
                </g>
            </g>
            <g transform="rotate(60)">
                <g class="aperture-leaf" style="transform:rotate(9.4deg);" clip-path="url(#aperture-visible-face)">
                    <path class="aperture-blade" style="fill:#0a0a0a" d="M 260 -103.923 L -40 415.692 C 90 500 160 710 20.94 1199.82 A 1200 1200 0 0 0 1087.57 507.14 L 906.31 422.62 C 650 180 400 -50 260 -103.923 Z" />
                    <path class="aperture-rim" d="M -40 415.692 L 260 -103.923 C 400 -50 650 180 906.31 422.62" />
                </g>
            </g>
            <g transform="rotate(120)">
                <g class="aperture-leaf" style="transform:rotate(9.4deg);" clip-path="url(#aperture-visible-face)">
                    <path class="aperture-blade" style="fill:#0b0b0b" d="M 260 -103.923 L -40 415.692 C 90 500 160 710 20.94 1199.82 A 1200 1200 0 0 0 1087.57 507.14 L 906.31 422.62 C 650 180 400 -50 260 -103.923 Z" />
                    <path class="aperture-rim" d="M -40 415.692 L 260 -103.923 C 400 -50 650 180 906.31 422.62" />
                </g>
            </g>
            <g transform="rotate(180)">
                <g class="aperture-leaf" style="transform:rotate(9.4deg);" clip-path="url(#aperture-visible-face)">
                    <path class="aperture-blade" style="fill:#0e0e0e" d="M 260 -103.923 L -40 415.692 C 90 500 160 710 20.94 1199.82 A 1200 1200 0 0 0 1087.57 507.14 L 906.31 422.62 C 650 180 400 -50 260 -103.923 Z" />
                    <path class="aperture-rim" d="M -40 415.692 L 260 -103.923 C 400 -50 650 180 906.31 422.62" />
                </g>
            </g>
            <g transform="rotate(240)">
                <g class="aperture-leaf" style="transform:rotate(9.4deg);" clip-path="url(#aperture-visible-face)">
                    <path class="aperture-blade" style="fill:#0a0a0a" d="M 260 -103.923 L -40 415.692 C 90 500 160 710 20.94 1199.82 A 1200 1200 0 0 0 1087.57 507.14 L 906.31 422.62 C 650 180 400 -50 260 -103.923 Z" />
                    <path class="aperture-rim" d="M -40 415.692 L 260 -103.923 C 400 -50 650 180 906.31 422.62" />
                </g>
            </g>
            <g transform="rotate(300)">
                <g class="aperture-leaf" style="transform:rotate(9.4deg);" clip-path="url(#aperture-visible-face)">
                    <path class="aperture-blade" style="fill:#0d0d0d" d="M 260 -103.923 L -40 415.692 C 90 500 160 710 20.94 1199.82 A 1200 1200 0 0 0 1087.57 507.14 L 906.31 422.62 C 650 180 400 -50 260 -103.923 Z" />
                    <path class="aperture-rim" d="M -40 415.692 L 260 -103.923 C 400 -50 650 180 906.31 422.62" />
                </g>
            </g>
        </g>
        <g transform="rotate(0)">
            <g class="aperture-leaf" style="transform:rotate(9.4deg);">
                <g clip-path="url(#aperture-visible-face)">
                    <g clip-path="url(#aperture-outside-face)">
                        <path class="aperture-contact" transform="translate(1 2)" d="M -40 415.692 L 260 -103.923 C 400 -50 650 180 906.31 422.62" />
                    </g>
                </g>
            </g>
        </g>
        <g transform="rotate(60)">
            <g class="aperture-leaf" style="transform:rotate(9.4deg);">
                <g clip-path="url(#aperture-visible-face)">
                    <g clip-path="url(#aperture-outside-face)">
                        <path class="aperture-contact" transform="translate(1 2)" d="M -40 415.692 L 260 -103.923 C 400 -50 650 180 906.31 422.62" />
                    </g>
                </g>
            </g>
        </g>
        <g transform="rotate(120)">
            <g class="aperture-leaf" style="transform:rotate(9.4deg);">
                <g clip-path="url(#aperture-visible-face)">
                    <g clip-path="url(#aperture-outside-face)">
                        <path class="aperture-contact" transform="translate(1 2)" d="M -40 415.692 L 260 -103.923 C 400 -50 650 180 906.31 422.62" />
                    </g>
                </g>
            </g>
        </g>
        <g transform="rotate(180)">
            <g class="aperture-leaf" style="transform:rotate(9.4deg);">
                <g clip-path="url(#aperture-visible-face)">
                    <g clip-path="url(#aperture-outside-face)">
                        <path class="aperture-contact" transform="translate(1 2)" d="M -40 415.692 L 260 -103.923 C 400 -50 650 180 906.31 422.62" />
                    </g>
                </g>
            </g>
        </g>
        <g transform="rotate(240)">
            <g class="aperture-leaf" style="transform:rotate(9.4deg);">
                <g clip-path="url(#aperture-visible-face)">
                    <g clip-path="url(#aperture-outside-face)">
                        <path class="aperture-contact" transform="translate(1 2)" d="M -40 415.692 L 260 -103.923 C 400 -50 650 180 906.31 422.62" />
                    </g>
                </g>
            </g>
        </g>
        <g transform="rotate(300)">
            <g class="aperture-leaf" style="transform:rotate(9.4deg);">
                <g clip-path="url(#aperture-visible-face)">
                    <g clip-path="url(#aperture-outside-face)">
                        <path class="aperture-contact" transform="translate(1 2)" d="M -40 415.692 L 260 -103.923 C 400 -50 650 180 906.31 422.62" />
                    </g>
                </g>
            </g>
        </g>
    </g>
    <circle class="aperture-housing" r="980" />
    <g class="aperture-bezel-edge">
        <circle class="aperture-lit-bevel" r="900" />
        <circle r="1060" />
    </g>
</svg>
            <div class="aperture-focus-ring" :class="{ 'continuous-ring': continuous }" :style="{ transform: 'rotate(' + ringRotation + 'deg)', animationPlayState: ringAnimationState }">
<svg class="aperture-rim-scene" viewBox="-1100 -1100 2200 2200" focusable="false">
    <defs>
        <path id="aperture-rim-name-arc" d="M -982 0 A 982 982 0 0 1 982 0" />
    </defs>
    <g class="aperture-rim-details">
    <g class="aperture-index-light">
        <path v-for="angle in [-118, -78, -38]" :key="angle" :transform="'rotate(' + angle + ')'" d="M -3 -1020 H 3 M 0 -1023 V -1017" />
    </g>
        <g class="aperture-machining">
            <circle r="917" />
            <circle r="923" />
            <circle r="930" />
            <circle r="1000" />
            <circle r="1005" />
            <circle r="1052" />
        </g>
        <g class="aperture-grip">
    <path v-for="rib in 180" :key="rib" :transform="'rotate(' + rib * 2 + ')'" d="M 0 -1030 V -1048" />
</g>
        <g class="aperture-engraving">
            <text>
                <textPath href="#aperture-rim-name-arc" startOffset="18%">SHUTTERSPOOL · APERTURE</textPath>
            </text>
            <text transform="rotate(-115)" x="0" y="-951" text-anchor="middle">4</text>
            <text transform="rotate(-101)" x="0" y="-951" text-anchor="middle">4.5</text>
            <text transform="rotate(-87)" x="0" y="-951" text-anchor="middle">5</text>
            <text transform="rotate(-73)" x="0" y="-951" text-anchor="middle">5.6</text>
            <text transform="rotate(-59)" x="0" y="-951" text-anchor="middle">8</text>
            <text transform="rotate(-45)" x="0" y="-951" text-anchor="middle">11</text>
            <text transform="rotate(-31)" x="0" y="-951" text-anchor="middle">16</text>
        </g>
        <g class="aperture-fasteners">
            <g transform="rotate(68)">
                <circle cx="0" cy="-1000" r="6" />
                <path d="M -3 -1003 L 3 -997" />
            </g>
            <g transform="rotate(128)">
                <circle cx="0" cy="-1000" r="6" />
                <path d="M -3 -1003 L 3 -997" />
            </g>
            <g transform="rotate(188)">
                <circle cx="0" cy="-1000" r="6" />
                <path d="M -3 -1003 L 3 -997" />
            </g>
            <g transform="rotate(248)">
                <circle cx="0" cy="-1000" r="6" />
                <path d="M -3 -1003 L 3 -997" />
            </g>
            <g transform="rotate(308)">
                <circle cx="0" cy="-1000" r="6" />
                <path d="M -3 -1003 L 3 -997" />
            </g>
            <g transform="rotate(368)">
                <circle cx="0" cy="-1000" r="6" />
                <path d="M -3 -1003 L 3 -997" />
            </g>
        </g>
        <g class="aperture-wireframe">
            <circle r="1040" />
            <circle r="1010" />
            <circle class="aperture-encoder" r="945" />
            <circle r="910" />
            <path d="M -1070 0 H -1020 M 0 -1070 V -1020 M 1070 0 H 1020 M 0 1070 V 1020" />
        </g>
        <g class="aperture-calibration">
            <circle transform="rotate(60)" cx="0" cy="-975" r="3" />
            <circle transform="rotate(120)" cx="0" cy="-975" r="3" />
            <circle transform="rotate(180)" cx="0" cy="-975" r="3" />
            <circle transform="rotate(240)" cx="0" cy="-975" r="3" />
            <circle transform="rotate(300)" cx="0" cy="-975" r="3" />
            <circle transform="rotate(360)" cx="0" cy="-975" r="3" />
            <path v-for="index in 36" :key="`minor-${index}`" :transform="`rotate(${index * 10})`" d="M 0 -1020 V -1027" />
            <path v-for="index in 12" :key="`major-${index}`" :transform="`rotate(${index * 30})`" d="M 0 -1017 V -1035" />
        </g>
    </g>
</svg>
            </div>
        </div>
    </div>
</template>

<style scoped>
.aperture-background { position: fixed; inset: 0; z-index: -1; overflow: hidden; pointer-events: none; background: #080808; }
.aperture-lens { position: absolute; width: max(140vw, 160vh); height: max(140vw, 160vh); left: 86%; top: 88%; transform: translate(-50%, -50%); }
.aperture-iris, .aperture-focus-ring, .aperture-rim-scene { position: absolute; inset: 0; width: 100%; height: 100%; }
.aperture-focus-ring { will-change: transform; transition: transform var(--aperture-duration) cubic-bezier(.45, 0, .2, 1); }
.continuous-ring { animation: sign-in-ring 900s linear infinite; }
.continuous-ring .aperture-machining { opacity: .25; }
.continuous-ring .aperture-grip { opacity: .3; }
.continuous-ring .aperture-engraving { opacity: .28; }
.continuous-ring .aperture-fasteners { opacity: .35; }
.continuous-ring .aperture-wireframe { stroke: #527dff; opacity: .13; }
.continuous-ring .aperture-calibration { stroke: #527dff; opacity: .23; }
.continuous-ring .aperture-index-light { stroke: #527dff; opacity: 1; }
@keyframes sign-in-ring { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .continuous-ring { animation: none; } }
.aperture-leaf { transform-origin: 906.31px 422.62px; }
.aperture-neighbor-clip { transform-origin: 0 0; }
.aperture-contact { fill: none; stroke: #30343b; stroke-width: 1; opacity: .4; vector-effect: non-scaling-stroke; }
.aperture-rim { fill: none; stroke: #61718c; opacity: .11; stroke-width: 1.15; vector-effect: non-scaling-stroke; }
.aperture-housing { fill: none; stroke: #101010; stroke-width: 160; }
.aperture-lit-bevel { stroke-width: 1.5; }
.aperture-machining { fill: none; stroke: #303030; stroke-width: 1; opacity: .2; }
.aperture-grip { fill: none; stroke: #303030; stroke-width: 1; opacity: .24; }
.aperture-engraving { fill: #7a838f; font-family: inherit; font-size: 11px; font-weight: 500; letter-spacing: 2px; opacity: .2; }
.aperture-fasteners { fill: #0b0b0b; stroke: #535353; stroke-width: 1; opacity: .28; }
.aperture-fasteners path { stroke: #858585; }
.aperture-bezel-edge { fill: none; stroke: #61718c; stroke-width: 1; opacity: .13; }
.aperture-wireframe { fill: none; stroke: #61718c; stroke-width: 1; vector-effect: non-scaling-stroke; opacity: .09; }
.aperture-encoder { stroke-dasharray: 48 8 2 8; opacity: .65; }
.aperture-calibration { fill: none; stroke: #61718c; stroke-width: 1; opacity: .16; }
.aperture-index-light { fill: none; stroke: #7c91ba; stroke-width: 1.2; stroke-linecap: round; opacity: .6; }
.aperture-index-light path { vector-effect: non-scaling-stroke; }
</style>
