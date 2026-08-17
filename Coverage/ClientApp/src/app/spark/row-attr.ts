import type { PersistentObject } from '@mintplayer/ng-spark/models';

/**
 * Reads a sibling attribute off a renderer's `item` row, which is a
 * PersistentObject in query-list/sub-query/detail hosts and a flat record in
 * AsDetail sub-table cells (Spark#245 contract).
 */
export function rowAttr(item: unknown, name: string): any {
  if (!item) return undefined;
  const po = item as PersistentObject;
  if (Array.isArray(po.attributes)) {
    return po.attributes.find((a) => a.name === name)?.value;
  }
  return (item as Record<string, any>)[name];
}
