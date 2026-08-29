/**
 * Where Home lives, in one place.
 *
 * Home stopped being a hand-written page and became a virtual persistent object, so its URL is
 * now derived from `programUnits.json` — `/po/{alias}/{objectId}` — rather than chosen by the
 * router. Several call sites need it as a post-sign-in return URL, and one needs the accounts
 * query's alias to invoke Resync against. Both are strings that must agree with the server's
 * JSON, so they are named once here instead of being spelled out at each site.
 */
export const HOME_ROUTE = {
  /** `alias` of the Home program unit's persistent object, from programUnits.json. */
  poAlias: 'home',
  /** `objectId` of the Home program unit. HomeActions ignores it — there is exactly one Home. */
  objectId: 'main',
  /**
   * The virtual type the accounts rows belong to, and the type Resync/{Type} is granted on.
   *
   * ⚠️ This is what `executeCustomAction` wants as its FIRST argument — an object *type*, not a
   * query. Passing the query alias there yields a 404 from `/spark/actions/{type}/{action}`,
   * because no entity type resolves under that name.
   */
  accountsType: 'MyAccountRow',
  /** `alias` of MyAccountRow's my-accounts query, from MyAccountRow.json. */
  accountsQueryAlias: 'my-accounts',
} as const;

/** The Home page's absolute URL. */
export const HOME_URL = `/po/${HOME_ROUTE.poAlias}/${HOME_ROUTE.objectId}`;
