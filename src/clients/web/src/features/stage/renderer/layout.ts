/**
 * Pure line-layout module (docs/ARCHITECTURE.md §6.1). The canvas layer supplies the text measurer;
 * this module never touches the DOM.
 */

export const DEFAULT_MAX_LINE_WIDTH_EM = 30;

export interface LayoutSyllableInput {
  text: string;
  flatIndex: number;
  connectsToNext: boolean;
  lineBreakAfter: boolean;
}

export interface LayoutSourceLine {
  syllables: readonly LayoutSyllableInput[];
}

export interface LayoutSyllable {
  text: string;
  flatIndex: number;
  x: number;
  width: number;
  connectsToNext: boolean;
  lineBreakAfter: boolean;
}

export interface LayoutRow {
  sourceLineIndex: number;
  syllables: LayoutSyllable[];
  width: number;
}

export interface LayoutResult {
  rows: LayoutRow[];
  ch: number;
  em: number;
  maxWidth: number;
}

export type TextMeasurer = (text: string) => number;

/**
 * Gap before a syllable inside a row: none inside a `connectsToNext` chain, 2·ch after a chain
 * end, and 1·ch between standalone words.
 */
export function gapBeforeSyllable(
  previous: LayoutSyllableInput,
  beforePrevious: LayoutSyllableInput | undefined,
  ch: number,
): number {
  if (previous.connectsToNext) {
    return 0;
  }

  if (beforePrevious && beforePrevious.connectsToNext) {
    return 2 * ch;
  }

  return ch;
}

export function layoutLines(
  lines: readonly LayoutSourceLine[],
  measure: TextMeasurer,
  em: number,
  maxLineWidthEm: number = DEFAULT_MAX_LINE_WIDTH_EM,
): LayoutResult {
  const ch = measure('0');
  const maxWidth = maxLineWidthEm * em;
  const rows: LayoutRow[] = [];

  for (let sourceLineIndex = 0; sourceLineIndex < lines.length; sourceLineIndex++) {
    const syllables = lines[sourceLineIndex].syllables;
    let rowSyllables: LayoutSyllable[] = [];
    let rowWidth = 0;

    const flushRow = () => {
      if (rowSyllables.length > 0) {
        rows.push({
          sourceLineIndex,
          syllables: rowSyllables,
          width: rowWidth,
        });
        rowSyllables = [];
        rowWidth = 0;
      }
    };

    for (let index = 0; index < syllables.length; index++) {
      const syllable = syllables[index];
      const previous = index > 0 ? syllables[index - 1] : undefined;
      const beforePrevious = index > 1 ? syllables[index - 2] : undefined;
      const width = measure(syllable.text);
      const gap = rowSyllables.length === 0 || !previous
        ? 0
        : gapBeforeSyllable(previous, beforePrevious, ch);

      if (rowSyllables.length > 0 && rowWidth + gap + width > maxWidth) {
        flushRow();
      }

      const x = rowSyllables.length === 0 ? 0 : rowWidth + gap;
      rowSyllables.push({
        text: syllable.text,
        flatIndex: syllable.flatIndex,
        x,
        width,
        connectsToNext: syllable.connectsToNext,
        lineBreakAfter: syllable.lineBreakAfter,
      });
      rowWidth = x + width;

      if (syllable.lineBreakAfter) {
        flushRow();
      }
    }

    flushRow();
  }

  return { rows, ch, em, maxWidth };
}
