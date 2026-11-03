# Bundled transcription components

- Whisper and multilingual base weights: MIT, OpenAI. Original sources and license: https://github.com/openai/whisper and https://github.com/openai/whisper/blob/main/LICENSE
- whisper.cpp converted model, pinned revision 5359861c739e955e79d9a303bcbc70fb988958b1: https://huggingface.co/ggerganov/whisper.cpp ; model SHA-256: 60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe.
- Whisper.net and CPU runtimes 1.9.1: MIT, Sandro Hanea and contributors. https://github.com/sandrohanea/whisper.net ; license: https://github.com/sandrohanea/whisper.net/blob/main/LICENSE
- FFmpeg executables are from the checksum-pinned imageio-ffmpeg 0.6.0 wheels, not from the machine's PATH. https://github.com/imageio/imageio-ffmpeg/tree/v0.6.0 ; the Python wrapper is not used at runtime. The included FFmpeg builds use GPL components; see GPL-3.txt, their `-version`/`-buildconf` output and upstream binary/build provenance at https://github.com/imageio/imageio-binaries/tree/master/ffmpeg . FFmpeg source releases: https://ffmpeg.org/releases/ . Preserve applicable license texts and corresponding source/build information when redistributing binaries.

The asset preparation tool also copies license files from the pinned decoder wheels. All these notices are embedded in the executable and extracted alongside its decoder on first transcription.
