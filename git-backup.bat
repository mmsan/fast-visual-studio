@echo off
setlocal
cd /d "%~dp0"

git init || exit /b 1
git branch -M master || exit /b 1
git add . || exit /b 1

git diff --cached --quiet
if errorlevel 1 git commit -m "backup" || exit /b 1

git remote get-url origin >nul 2>&1
if errorlevel 1 (
  git remote add origin https://gitee.com/mmsan/fast-visual-studio.git || exit /b 1
) else (
  git remote set-url origin https://gitee.com/mmsan/fast-visual-studio.git || exit /b 1
)

git push -u origin master || exit /b 1
exit /b 0
