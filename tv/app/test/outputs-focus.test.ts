import assert from 'node:assert/strict'
import { describe, it } from 'node:test'
import { EDGE_LEFT, keyOf, step } from '../src/focus.ts'
import { outputsFocusGrid } from '../src/screens/outputsFocus.ts'

describe('Outputs remote navigation', () => {
  for (const espOn of [false, true]) {
    it(`reaches full-frame and connection controls with ESP32 ${espOn ? 'on' : 'off'}`, () => {
      const grid = outputsFocusGrid(espOn, true)
      assert.equal(step(grid, 'hdr-priority', 'down'), 'hdr-fullframe')
      assert.equal(step(grid, 'hdr-fullframe', 'up'), 'hdr-priority')
      assert.equal(step(grid, 'hdr-fullframe', 'down'), 'hdr-test')
      assert.equal(step(grid, 'hdr-fullframe', 'left'), espOn ? 'esp-search' : 'esp-switch')
      assert.equal(step(grid, 'esp-switch', 'left'), EDGE_LEFT)
      assert.equal(step(grid, 'hdr-test', 'down'), null)
    })
  }

  it('skips the opt-in when an older service does not report support', () => {
    const grid = outputsFocusGrid(false, false)
    assert.equal(step(grid, 'hdr-priority', 'down'), 'hdr-test')
  })

  it('maps remote Select and Return keys', () => {
    assert.equal(keyOf({ keyCode: 13 } as KeyboardEvent), 'ok')
    assert.equal(keyOf({ keyCode: 10009 } as KeyboardEvent), 'back')
  })
})
