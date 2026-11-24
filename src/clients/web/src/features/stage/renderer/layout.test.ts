import { describe, expect, it } from 'vitest';
import { layoutLines, type LayoutSourceLine } from './layout';

const measure = (text: string): number => text.length;

function line(...syllables: Array<[text: string, connectsToNext?: boolean, lineBreakAfter?: boolean]>): LayoutSourceLine {
  return {
    syllables: syllables.map(([text, connectsToNext = false, lineBreakAfter = false], index) => ({
      text,
      flatIndex: index,
      connectsToNext,
      lineBreakAfter,
    })),
  };
}

describe('layoutLines', () => {
  it('uses zero gap inside a connectsToNext chain', () => {
    const result = layoutLines([line(['aa', true], ['bb'])], measure, 10);

    expect(result.rows).toHaveLength(1);
    expect(result.rows[0].syllables[0].x).toBe(0);
    expect(result.rows[0].syllables[1].x).toBe(2);
    expect(result.rows[0].width).toBe(4);
  });

  it('uses 2ch after a chain end and 1ch between unconnected words', () => {
    const chain = layoutLines([line(['aa', true], ['bb'], ['cc'])], measure, 10);
    expect(chain.rows[0].syllables[2].x).toBe(6);

    const standalone = layoutLines([line(['aa'], ['bb'])], measure, 10);
    expect(standalone.rows[0].syllables[1].x).toBe(3);
  });

  it('wraps once adding a syllable would exceed MaxLineWidthEm', () => {
    const result = layoutLines(
      [line(['aaaaaaaaaa'], ['bbbbbbbbbb'], ['cccccccccc'], ['dddddddddd'], ['eeeeeeeeee'])],
      measure,
      10,
      5,
    );

    expect(result.rows).toHaveLength(2);
    expect(result.rows[0].syllables).toHaveLength(4);
    expect(result.rows[1].syllables).toHaveLength(1);
    expect(result.rows[1].syllables[0].x).toBe(0);
  });

  it('honours lineBreakAfter as a forced break even when the row would fit', () => {
    const result = layoutLines([line(['aa', false, true], ['bb'])], measure, 10);

    expect(result.rows).toHaveLength(2);
    expect(result.rows[0].syllables.map((syllable) => syllable.text)).toEqual(['aa']);
    expect(result.rows[1].syllables.map((syllable) => syllable.text)).toEqual(['bb']);
  });

  it('starts every source line on a new row and tags its sourceLineIndex', () => {
    const result = layoutLines(
      [line(['aa', false, true]), line(['bb', false, true]), line(['cc'])],
      measure,
      10,
    );

    expect(result.rows.map((row) => row.sourceLineIndex)).toEqual([0, 1, 2]);
  });
});
