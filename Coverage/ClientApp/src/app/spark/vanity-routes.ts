import { SparkService } from '@mintplayer/ng-spark/services';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { rowAttr } from './row-attr';

/**
 * Canonical app routes for the entity types that have a purpose-built page.
 * Spark's generic grids and reference links always emit `/po/{type}/{id}`
 * (there is no link-resolver seam upstream yet), so the poDetail route
 * component sends those types on to the page below — the URL differs, the
 * content is the real product page rather than an attribute dump.
 *
 * Types absent from this map (Build) keep the generic detail page, which is
 * genuinely the best view they have.
 */
export type VanityRoute = any[] | null;

export async function resolveVanityRoute(
  spark: SparkService, entityTypeName: string, po: PersistentObject): Promise<VanityRoute> {
  switch (entityTypeName) {
    case 'Repository': {
      const repo = splitFullName(rowAttr(po, 'FullName'));
      return repo ? ['/r', repo.owner, repo.name] : null;
    }
    case 'Commit': {
      const sha = rowAttr(po, 'Sha');
      const repo = await loadRepository(spark, rowAttr(po, 'Repository'));
      return typeof sha === 'string' && sha && repo ? ['/r', repo.owner, repo.name, 'c', sha] : null;
    }
    case 'Account': {
      const login = rowAttr(po, 'Login');
      return typeof login === 'string' && login ? ['/a', login] : null;
    }
    default:
      return null;
  }
}

/**
 * The referenced repository's owner/name. Loads the document rather than
 * parsing the reference breadcrumb, which upstream can resolve to a different
 * document's label (Spark#251).
 */
async function loadRepository(spark: SparkService, repositoryId: unknown) {
  if (typeof repositoryId !== 'string' || !repositoryId) return null;
  try {
    const repo = await spark.get('Repository', repositoryId);
    return splitFullName(rowAttr(repo, 'FullName'));
  } catch {
    return null;
  }
}

function splitFullName(fullName: unknown): { owner: string; name: string } | null {
  if (typeof fullName !== 'string') return null;
  const [owner, name] = fullName.split('/');
  return owner && name ? { owner, name } : null;
}
