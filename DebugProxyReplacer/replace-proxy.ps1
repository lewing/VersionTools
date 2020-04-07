param ($asppath, $monopath)

Copy-Item -Path $monopath\\sdks\\wasm\\Mono.WebAssembly.DebuggerProxy\\*.cs -Destination $asppath\\src\\Components\\WebAssembly\\DebugProxy\\src\\MonoDebugProxy\\ws-proxy -Recurse -Exclude AssemblyInfo.cs