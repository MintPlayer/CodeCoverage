import { describe, expect, it } from 'vitest';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { rowAttr } from './row-attr';

function po(attributes: Record<string, unknown>): PersistentObject {
  return {
    attributes: Object.entries(attributes).map(([name, value]) => ({ name, value })),
  } as unknown as PersistentObject;
}

describe('rowAttr', () => {
  it('reads an attribute off a PersistentObject row', () => {
    expect(rowAttr(po({ Sha: 'abc123', Branch: 'master' }), 'Sha')).toBe('abc123');
  });

  // AsDetail sub-table cells hand the renderer a flat record instead (Spark#245).
  // A renderer that only understood one shape would silently render blank in the
  // other host, which looks like missing data rather than a wiring bug.
  it('reads the same attribute off a flat record row', () => {
    expect(rowAttr({ Sha: 'abc123' }, 'Sha')).toBe('abc123');
  });

  it('returns undefined for an attribute the row does not carry', () => {
    expect(rowAttr(po({ Sha: 'abc123' }), 'Missing')).toBeUndefined();
    expect(rowAttr({ Sha: 'abc123' }, 'Missing')).toBeUndefined();
  });

  it('returns undefined rather than throwing when there is no row', () => {
    expect(rowAttr(null, 'Sha')).toBeUndefined();
    expect(rowAttr(undefined, 'Sha')).toBeUndefined();
  });

  // A falsy attribute value is a value. Collapsing it to undefined would make
  // "zero lines covered" render as "unknown".
  it('preserves falsy attribute values', () => {
    expect(rowAttr(po({ LinesCovered: 0 }), 'LinesCovered')).toBe(0);
    expect(rowAttr({ LinesCovered: 0 }, 'LinesCovered')).toBe(0);
    expect(rowAttr(po({ Partial: false }), 'Partial')).toBe(false);
  });
});
