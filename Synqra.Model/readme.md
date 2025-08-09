This project contains a core model pieces shared between compile time generator and synqra runtime.

e.g. IBindableModel that is used to assign property value from strings or to inject a store into the model to let it emit commands.
Keep it small and simple, do not bring entire infrastructure here.
Only core interfaces.
This have to be netstandard2.0