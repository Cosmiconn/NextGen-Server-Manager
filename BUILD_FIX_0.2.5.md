# Runtime Fix 0.2.5

Root cause identified from Windows runtime stack trace:

`System.InvalidOperationException: TwoWay- oder OneWayToSource-Bindungen funktionieren nicht mit der schreibgeschützten Eigenschaft "LogText"`

## Fixes

- `LogText` TextBox binding changed to `Mode=OneWay`.
- `ActivityText` TextBox binding changed to `Mode=OneWay`.
- `PdbOutput` TextBox binding changed to `Mode=OneWay`.
- All status/diagnostic DataGrids are now `IsReadOnly=True` to prevent accidental source writes into status/record objects.
- Version bumped to 0.2.5.

The read-only TextBoxes are display surfaces. `IsReadOnly=True` on a WPF TextBox does **not** change the default `Text` binding mode; `TextBox.Text` defaults to TwoWay. Therefore the binding mode must be explicitly OneWay when the ViewModel setter is private.
