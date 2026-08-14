---
name: reclass
description: Reverse engineer a running process with ReClass.NET over MCP - attach, resolve address formulas, read memory, and record the discovered layout as named typed class fields that appear live in the ReClass.NET GUI. Use for struct discovery, offset hunting, PE header walks, and generating C++/C# headers from a live process.
---

# ReClass.NET over MCP

The tool descriptions tell you what each verb does. This tells you the loop.

## Ground rules

- `status` first, every session. It tells you the pointer size, whether a process is
  attached, whether mutations are allowed, and how many classes already exist. Do not
  guess any of that.
- Addresses in, addresses out, are hexadecimal strings. `0x1f4` and `1f4` are both accepted.
- Address formulas are not C. Module names must be wrapped in angle brackets and
  every number is hexadecimal, so `10` means `0x10`. `[x]` dereferences.
  - correct: `<game.exe>+0x1f4`, `[<game.exe>+0xdead]+0x10`, `[<game.exe>+0x3c,4]`
  - wrong: `game.exe+0x1f4`, `0n500`, `[[base]]`
  - The optional second argument of `[expr,4]` is the read width in bytes, 4 or 8. Use it
    when you dereference a 32-bit field in a 64-bit process.
- A class is identified by its uuid. A node is `<uuid>:<i>/<j>`, an index path rather
  than a name. Index paths shift after every insert or delete. Use the handles the
  mutating call returned, or re-read with `get_class`. Never reuse a handle you
  remember from three calls ago.
- Reads report failure honestly: an unreadable region comes back `success: false` with an
  error, never as zeroes. If you see zeroes, they are really zeroes.

## The discovery loop

1. `list_processes` with a `filter`, then `attach_process`.
2. `list_modules` to get the image base. `resolve_address` to turn a formula into an
   address and learn which module and section it lands in. That is how you tell a real
   pointer from a coincidence.
3. `read_memory` a window around the address you care about. Batch it: pass a `reads`
   array covering several offsets in one call instead of walking one field at a time.
4. `read_typed` with `type: "pointer"` on the candidate offsets. Every pointer result
   carries the module and section it points into. A field whose value points into `.text`
   is a function pointer or a vtable; one pointing into a private region is data;
   one pointing nowhere is not a pointer.
5. `create_class` at the address, then `add_node` with a batch of `{type, name}` entries
   in layout order. Offsets are computed for you as a running sum, so add fields in order
   and never try to set an offset directly.
6. `select_class` so the human's ReClass.NET window follows you.
7. `generate_code` when the layout is done.

## Recording a layout well

- Prefer building a class field by field with `add_node` over creating hex bytes and
  retyping them. It is fewer calls and the names land right the first time.
- `suggest_types` runs ReClass.NET's dissector without mutating and tells you what it
  would guess for each hex node. Read it, then decide. `dissect_nodes` applies the guesses
  in place, which is useful on a fresh hex blob and wasteful once you have real names.
- `change_node_type` only compensates shrinking. Replacing a 2-byte field with a
  4-byte one silently eats the next 2 bytes, and the result says so in a `warning`. When
  you see that warning, re-read the class and repair what follows before continuing.
- Changing a node to a text type loses its name and comment. Set the type first, then the
  name.
- An `EnumNode` defaults to a 4-byte dummy enum. Create the enum with `create_enum`
  (giving the real underlying `size`), then `bind_enum_node`, and the field snaps back to
  the right width.
- Enums are identified by name, not by a uuid. `rename_enum` breaks every node bound
  to it on the next project load; the tool warns you, and `bind_enum_node` is the repair.

## Safety

- `write_memory` and `write_typed` return the bytes they overwrote as `previous`. Keep
  that value if you might need to revert.
- Every mutating call snapshots the project first. `list_changes` shows the ring and
  `undo_last_change` restores it. ReClass.NET itself has no undo, so this is the only one.
- `control_process` with `terminate` kills the target and ends the session. There is no
  undo for that.
- If a destructive tool is missing from your tool list, mutations are switched off in the
  GUI; say so instead of trying to work around it.

## When something fails

- `is not a valid address formula`: you almost certainly forgot the angle brackets, or
  wrote a decimal number.
- `No process is attached`: call `list_processes` then `attach_process`; do not retry the
  failing call.
- `Node handle ... is out of range`: the tree changed under you. Re-read `get_class`.
- A result truncated with `_meta["net.reclass/truncated"]`: fetch the rest with
  `get_output` using the `outputId`, or narrow the request with `offset`/`limit`/`fields`.
- The tool told you which tool to call next. Read the `hint`.
