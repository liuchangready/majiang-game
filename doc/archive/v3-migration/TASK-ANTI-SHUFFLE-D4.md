# D4 minimal Kotlin compile fix

Own ONLY core/src/main/kotlin/com/majiang/presentation/ShufflePreparationModel.kt. No tests/build/shell. Fix root compilererrors line358/360: Smart cast to ShuffleSelection impossible because prep.selection is public API property declared in DIFFERENT module. Capture `val selected = prep.selection` before nullcheck, use selected inside, optionally deepCopy for storage. Do not change unrelated logic. Report delivery-ANTI-SHUFFLE-D4.md briefly. Root executes compile/tests. EarlierD3done.
