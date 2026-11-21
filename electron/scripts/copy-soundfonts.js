const { copyFileSync, cpSync } = require("node:fs")
const { resolve } = require("node:path")

cpSync(
  resolve(__dirname, "../../assets/soundfonts"),
  resolve(__dirname, "../assets/soundfonts"),
  { recursive: true },
)
copyFileSync(
  resolve(__dirname, "../../LICENSE"),
  resolve(__dirname, "../LICENSE"),
)
