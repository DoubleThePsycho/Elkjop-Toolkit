# Windows system-message dialog template

This version is intentionally built as a normal Visual Studio WinForms Designer form.

Open `SystemMessageDialogForm.cs` and choose **View Designer**. Every visual property is set in `SystemMessageDialogForm.Designer.cs` / the Properties window; the constructor does not build, position, style, or add any controls at runtime.

## Template measurements at 96 DPI

- Client area: 500 × 157
- Content area: white, 110 px high
- Footer: RGB 240,240,240, 47 px high
- Footer separator: RGB 223,223,223, 1 px
- Icon: 32 × 32 at X=24, Y=21
- Text left edge: X=72
- Main instruction: Segoe UI 12 pt, RGB 0,51,153
- Body: Segoe UI 9 pt, `SystemColors.ControlText`
- Button: 75 × 23, 21 px from right edge, 12 px from footer top
- Form: `FixedDialog`, no maximize/minimize/icon, DPI scaling enabled

## Changing the message type

The layout does not change. In the Designer code the sample icon is:

```csharp
messageIcon.Image = SystemIcons.Warning.ToBitmap();
```

Swap `Warning` for `Information` or `Error` as needed.

If you prefer an embedded resource instead, assign the PictureBox `Image` property in Visual Studio and Visual Studio will serialize it into the `.resx` file.
