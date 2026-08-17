import { SparkService } from '@mintplayer/ng-spark/services';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { rowAttr } from './row-attr';

/**
 * Entity types that still have a purpose-built page of their own; the poDetail
 * route forwards to it instead of rendering the generic detail.
 *
 * Repositories and commits are NOT here on purpose — they *are* the generic
 * Spark detail page now (their /r/... URLs forward the other way, see
 * vanity-redirects.ts). Only Account keeps a bespoke page, for upload-token
 * management.
 */
export type VanityRoute = any[] | null;

export async function resolveVanityRoute(
  _spark: SparkService, entityTypeName: string, po: PersistentObject): Promise<VanityRoute> {
  if (entityTypeName !== 'Account') return null;
  const login = rowAttr(po, 'Login');
  return typeof login === 'string' && login ? ['/a', login] : null;
}
