import type { FocusGrid } from '../focus.ts';

/** The two output columns and their remote-control navigation. */
export function outputsFocusGrid(espOn: boolean, fullFrameSupported: boolean): FocusGrid {
  const grid: FocusGrid = [
    [{ id: 'run-setup', col: 2 }],
    [{ id: 'esp-switch', col: 0 }, { id: 'hdr-switch', col: 2 }],
  ];
  if (espOn) {
    grid.push([
      { id: 'esp-blink', col: 0, nav: { down: 'esp-blink' } },
      { id: 'esp-search', col: 1, nav: { down: 'esp-search' } },
      { id: 'hdr-host', col: 2 },
    ]);
  } else {
    grid.push([{ id: 'hdr-host', col: 2, nav: { left: 'esp-switch' } }]);
  }
  const back = espOn ? 'esp-search' : 'esp-switch';
  grid.push([{ id: 'hdr-port', col: 2, nav: { left: back } }]);
  grid.push([{ id: 'hdr-priority', col: 2, nav: { left: back } }]);
  if (fullFrameSupported) grid.push([{ id: 'hdr-fullframe', col: 2, nav: { left: back } }]);
  grid.push([{ id: 'hdr-test', col: 2, nav: { left: back } }]);
  return grid;
}
