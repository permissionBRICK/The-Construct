import * as Schema from "effect/Schema";

/**
 * Free space on the disk the T3 server lives on (the Construct VM's disk).
 *
 * `availableBytes` is what an UNPRIVILEGED process can still write: Linux keeps the
 * last few percent of an ext4 volume for root, so non-root writes fail while `df`
 * still shows a little free space. That reserve is where things start breaking, so
 * the state is judged on the unprivileged figure.
 *  - `full`: the reserve is all that is left; non-root writes fail now or very soon.
 *  - `low`: still writable, but only a small margin remains.
 */
export const ConstructDiskSpaceState = Schema.Literals(["ok", "low", "full"]);
export type ConstructDiskSpaceState = typeof ConstructDiskSpaceState.Type;

export const ConstructDiskSpace = Schema.Struct({
  /** The directory that was measured (the server's home). */
  path: Schema.String,
  totalBytes: Schema.Number,
  /** Bytes an unprivileged process can still write. */
  availableBytes: Schema.Number,
  state: ConstructDiskSpaceState,
});
export type ConstructDiskSpace = typeof ConstructDiskSpace.Type;

export class ConstructDiskSpaceError extends Schema.TaggedErrorClass<ConstructDiskSpaceError>()(
  "ConstructDiskSpaceError",
  { message: Schema.String },
) {}
