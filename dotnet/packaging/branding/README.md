# Branding

`make-icons.ps1` draws each candidate mark as vector geometry and rasterises it
at every size Windows asks for (16 through 256), then packs the set into a
multi-resolution `.ico`. Detail is dropped below 24px on purpose: downscaling a
detailed mark turns it to mush in the taskbar, which is where an icon spends
most of its life.

```powershell
pwsh make-icons.ps1 -OutDir out
pwsh icon-sheet.ps1 -IconDir out -Out candidates.png   # compare at true size
```

No mark is chosen yet. Three are drawn: a map pin over contour lines, the pin
alone, and a compass rose. The pin alone is the most legible at 16px; the
compass rose is the strongest at 48px and up but is also Meridian's mark.

Generated `.ico` and `.png` files are not committed — regenerate them.
