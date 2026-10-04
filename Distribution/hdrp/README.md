# ViewWeave HDRP Git package

Install this folder from the repository using Unity Package Manager:

    https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main

Use Unity 6000.6.4f1 with the matching Unity HDRP render pipeline. Start with an ordinary Cube or Box, right-click the Main Camera in the Hierarchy, and choose ViewWeave > Bridge Rig to create a child rig; no sample import or object script is required. Bridge Worker output supports Windows D3D11 and D3D12 Editor Play and Player. D3D12 shares GPU resources with a D3D11 worker on the same adapter and does not read image pixels back to the CPU. Install and run the Looking Glass Bridge runtime separately. Legacy Direct output is D3D11-only. The optional Windows sample is imported only through Package Manager Samples.
