"""Validate the launcher's exported API graphs with the pinned native ComfyUI validator on CPU."""

import argparse
import asyncio
import json
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
import uuid


async def validate(checkout, graphs, scratch):
    sys.path.insert(0, str(checkout))
    import comfy.options

    sys.argv[1:] = ["--cpu", "--base-directory", str(scratch)]
    comfy.options.enable_args_parsing()
    import comfyui_version
    import folder_paths
    import nodes
    import execution
    from PIL import Image

    if comfyui_version.__version__ != "0.46.0":
        raise RuntimeError(f"Expected ComfyUI 0.46.0, found {comfyui_version.__version__}")

    # Only filenames are needed by the native loader schemas. Nothing here submits inference.
    model_files = {
        "diffusion_models": [
            "qwen_image_2512_fp8_e4m3fn.safetensors",
            "qwen_image_edit_2511_fp8mixed.safetensors",
        ],
        "text_encoders": ["qwen_2.5_vl_7b_fp8_scaled.safetensors"],
        "vae": ["qwen_image_vae.safetensors"],
    }
    for category, names in model_files.items():
        folder = scratch / "models" / category
        folder.mkdir(parents=True, exist_ok=True)
        for name in names:
            (folder / name).touch()
        folder_paths.add_model_folder_path(category, str(folder), is_default=True)
    inputs = scratch / "input"
    inputs.mkdir(exist_ok=True)
    folder_paths.set_input_directory(str(inputs))
    Image.new("RGB", (16, 16)).save(inputs / "blockcraft_fixture.png")
    await nodes.init_extra_nodes(init_custom_nodes=False, init_api_nodes=False)

    for name in ("generate", "edit"):
        graph = json.loads((graphs / f"{name}.json").read_text(encoding="utf-8-sig"))
        ok, error, outputs, node_errors = await execution.validate_prompt(str(uuid.uuid4()), graph, None)
        if not ok or node_errors or len(outputs) != 1 or graph[next(iter(outputs))]["class_type"] != "PreviewImage":
            raise RuntimeError(f"{name} failed ComfyUI validation: {error!r}, {node_errors!r}, outputs={outputs!r}")
        print(f"ComfyUI {comfyui_version.__version__} accepted the launcher {name} graph ({len(graph)} nodes).")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("checkout", type=Path, help="Checkout of ComfyUI with its Python dependencies installed")
    parser.add_argument("graphs", type=Path, help="Directory written by LocalQwenImageChecks.ExportGraphs")
    args = parser.parse_args()
    with TemporaryDirectory(prefix="blockcraft-comfy-validation-") as temporary:
        asyncio.run(validate(args.checkout.resolve(), args.graphs.resolve(), Path(temporary)))
