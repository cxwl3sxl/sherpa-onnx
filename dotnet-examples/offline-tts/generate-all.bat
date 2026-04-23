@echo off
chcp 65001 >nul
echo 开始生成所有 174 个说话人样本...

for /L %%i in (0,1,173) do (
    echo 生成说话人 %%i ...
    dotnet run -c Release --vits-model=./vits-icefall-zh-aishell3/model.onnx --tokens=./vits-icefall-zh-aishell3/tokens.txt --lexicon=./vits-icefall-zh-aishell3/lexicon.txt --tts-rule-fsts=./vits-icefall-zh-aishell3/phone.fst,./vits-icefall-zh-aishell3/date.fst,./vits-icefall-zh-aishell3/number.fst --tts-rule-fars=./vits-icefall-zh-aishell3/rule.far --sid=%%i --output-filename=./speakers/sid-%%i.wav --text "你好，我是小米的语音助手，很高兴为你服务。"
)

echo 完成！所有 174 个说话人样本已生成到 ./speakers/ 目录
pause
