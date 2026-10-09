[日本語](ja/bridge-output-lifecycle.md)

# Bridge output lifecycle

Edit Mode device preview and Play output share one process-level Looking Glass Bridge endpoint. Each mode gets an exclusive, generation-scoped lease to submit quilts. Closing a mode returns its lease; it does not normally destroy the process-level SDK session, registered primary texture, or hidden Bridge endpoint.

This page distinguishes the current Native artifact/source implementation from the sample projects and from verified device behavior. The current source implements an explicit Busy admission result and is included in the generated distribution packages. The reverse Play-to-Edit black/delayed output issue remains unresolved.

## Process ownership and mode leases

The process owner keeps the Native Bridge session, worker thread/window, SDK-registered primary texture, and fixed-profile SDK input alive between mode owners. The endpoint is hidden and parked when no lease owns output. An Edit Preview or Play lease owns that generation's Unity producer, camera state, variable-size quilt/transfer resources, and their GPU work. Only one mode owns the endpoint at a time.

```mermaid
flowchart LR
    subgraph Process[Unity process lifetime]
        subgraph Lease[One exclusive mode lease]
            Mode[Play or Edit Mode preview]
            Producer[Unity producer and quality-sized quilt]
        end
        Stable[Fixed-profile SDK input]
        Primary[SDK-registered primary texture]
        Endpoint[Native Bridge session, worker, and hidden window]
    end
    Mode --> Producer
    Producer -->|GPU copy or per-tile scale| Stable
    Stable --> Primary
    Endpoint --> Primary
    Primary --> Bridge[Looking Glass Bridge runtime]
    Bridge --> Display[Looking Glass display]
```

The registered primary texture and SDK-facing input retain stable identities across lease generations. Producer quilt dimensions may change with quality while the logical device grid remains fixed. Full process shutdown is a separate boundary: it closes lease admission and permits final SDK/native cleanup only after the matching module/registry and Unity GPU cleanup proofs are available.

## Pause, resume, close, and handoff

| Action | Mode producer and Unity textures | Lease | Process Bridge endpoint |
| --- | --- | --- | --- |
| Pause Preview | Retained after submissions drain | Same generation retained | Hidden and parked |
| Resume Preview | Reused | Same generation resumes | Shown through the same endpoint |
| Close Preview | Retired after native source-use and managed/Core GPU proofs | Return proof, resource retirement, then ACK | Hidden and parked; next lease waits for return/ACK |
| Edit/Play handoff | Old producer and textures are retired before the next mode owns them | New exclusive generation after old-generation handoff | Same endpoint remains parked between owners |
| Process shutdown | Retired after required Unity GPU proof | Admission closes; final teardown proof can replace normal handoff ACK | SDK endpoint, registered textures, and worker are released only at final cleanup |

Normal return order is strict. Native first reports `ReturnedProven` for the old source textures. Managed code then waits for Core/transfer and producer GPU completion, retires the old mode textures and ownership, and sends the generation ACK. Native allows the next lease only after that ACK. A Native status query is historical metadata; it is not the managed ACK and does not prove a current output owner or visible frame. A producer/GPU completion count also does not prove Bridge scanout.

```mermaid
sequenceDiagram
    participant Old as Current mode
    participant Unity as Unity producer and GPU
    participant Worker as Native worker
    participant Endpoint as Process Bridge endpoint
    participant Next as Next mode
    Old->>Worker: Gate submissions and request return for generation N
    Worker->>Endpoint: Drain source work and hide endpoint
    Endpoint-->>Worker: Native source-use proof
    Worker-->>Old: ReturnedProven for generation N
    Old->>Unity: Wait for Core/transfer and producer GPU proofs
    Unity-->>Old: GPU work complete
    Old->>Unity: Retire mode textures and ownership
    Old->>Worker: ACK generation N after Unity retirement
    Worker-->>Next: Permit next generation acquisition
    Next->>Endpoint: Acquire exclusive generation N+1
    Endpoint-->>Next: Grant same parked endpoint
    Next->>Worker: Submit with new mode resources
```

Unity reload and normal Stop return an active mode lease through this path; they do not uninitialize the process SDK session. For process shutdown, admission closes. Final cleanup may release resources only after the matching Native module/registry teardown and required Unity GPU proof are established. Normal handoff ACK is not required once final teardown itself is proven.

## Native admission outcomes

The current Native source candidate adds a lease-only draw-data admission call. It validates the lease identity, lifecycle state, quilt descriptor, and fixed logical grid before reporting an outcome.

```mermaid
flowchart TD
    Submit[Submit quilt for current lease] --> Valid{Valid active Ready lease and request?}
    Valid -->|No| Reject[Reject as invalid, stale, or Native failure]
    Valid -->|Yes| Queue{Three-token admission queue full?}
    Queue -->|No| Accept[Accept draw token; retain source texture]
    Queue -->|Yes| Busy[Return Busy; allocate no draw payload or texture reference]
    Busy --> Policy{Managed caller policy}
    Policy -->|Edit Preview| Retry[Retry while keeping current owner and generation]
    Policy -->|Playback| Skip[Skip this frame and continue playback]
    Reject --> Stop[Stop or clean up the failed mode; keep resources until proven safe]
```

`Accepted` means the Native queue accepted a draw token; it does not prove GPU completion or display presentation. `Busy` is reserved for a valid, ready, running lease whose three-token admission queue is full. It does not AddRef the submitted texture or allocate a draw payload. Managed Edit Preview retries while retaining its current producer, endpoint, and generation. Quilt playback skips only that frame and continues. Invalid arguments, stale generations, lifecycle conflicts, and Native faults remain errors; they do not follow the retry path.

The current source candidate also resets Native worker submission/completion/drop statistics when a new lease generation is acquired. These values are diagnostic counters, not source-retirement, Unity GPU-release, ACK, or display-scanout proofs. Managed submission attempts, Native accepted submissions, and completed worker work are different events and should not be compared as if they were one counter. In particular, the older A6A DLL loaded by the samples exposed process-scope counters; those observations are not per-generation evidence.

An incompatible adapter, device, logical quilt grid, output format outside the supported RGBA8 typeless/UNORM/SRGB family, or Native profile may require closing output and restarting the Unity Editor. This path does not promise Native profile/device hot-swap.

## Quality and rendered view density

`Preview`, `Balanced`, and `Native` quality profiles can change producer quilt dimensions after prior GPU work drains. `Full`, `Half`, and `Quarter` view density change the number of rendered views while preserving the completed quilt grid. Native copies same-size inputs or performs GPU per-tile scaling into the fixed-profile SDK input. Each tile clamps sampling to its own view, preventing pixels from adjacent views from bleeding into it.

The fixed input can add GPU copy/scaling cost. Fewer rendered views or smaller source quilts do not promise a proportional increase in transport frame rate. The latest Native copy/scale tests are fake/WARP tests, not a performance benchmark. The internal mode lease does not add a public SDK component or a new public control API.

## Candidate and installed build identity

Both sample projects were cold-verified on 2026-10-09 with package commit `eb527e1ec126ec99d35e9990d8dce0119b855b4d` and Native DLL SHA-256 `A6A062343710AF2362DAF7B923A9BB98BDD5D381D896C68AE3190CA55655FBC7`. This is dated sample evidence, not a claim about their current contents after later package updates. The current Native artifact/source implementation has DLL SHA-256 `655FDB898CF7B9F9733A1880828B740B62FB74E0C3ECDF9D825C07794F6388CD`, size 208,896 bytes, and 51 exports. Its Native Windows fake/WARP CTest suite passed 12/12, source review passed, and the artifact is included in the generated distribution packages. This artifact was not installed in the sample Editors during the 2026-10-09 device validation.

Current-source Roslyn compilation passed for Core and Bridge runtime Editor/Player configurations, `Bridge.Editor`, and the focused `QuiltPlaybackSafetyTests` source. This was compilation only; Unity Test Runner tests were not executed. Native tests use fake/WARP resources and do not load the official Looking Glass Bridge SDK or verify a physical display.

## Bounded URP device observations

All following physical observations used Windows Unity 6000.6.4f1, the updated URP sample, package commit `eb527e1`, and installed DLL A6A...FBC7. They are not evidence for the newer 655F... candidate. HDRP device output was not tested.

| Transition | Device observation | Managed/lease observation | Result boundary |
| --- | --- | --- | --- |
| Edit Preview Close/Start, then active Edit Preview → Play | On the URP sample, the user confirmed visible output and fresh move/Undo in both modes | The same process endpoint was reused across each handoff | One Close/Start and one Edit-to-Play transition were confirmed on the 2026-10-09 installed build |
| Active Play → Edit Preview | Initially black; after one producer-quilt readback, the user reported visible output on the same lease | No new endpoint or lease was created during the observed recovery | The readback may have affected timing; the cause of the black interval is unknown and repeatable recovery is not established |
| A later reverse handoff | The user later reported visible output, but a subsequent poll found the Editor worker stopped with an error and no current owner | Live output after that report was not established | The image report does not prove recovery; the reverse handoff remains unresolved |

These observations used the sample identity dated above and do not test the current Native artifact/source implementation on hardware. Native fake/WARP tests passed 12/12. The current-source runtime Editor/Player configurations, `Bridge.Editor`, and focused playback-safety test source compiled; Unity Test Runner was not run. The new Native artifact has not been physically tested. HDRP hardware handoffs, device hot-plug, physical size, optical image quality, and runtime performance remain unverified.
