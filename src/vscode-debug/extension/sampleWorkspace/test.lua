local printer = require("printer")
local x = 2
local y = 3

local function add(i)
  local z = x + y + i
  return z
end

for i = 1, 10, 3 do
  local z = add(i)
  print("x + y + i:", z)
  x  = x + 1
end

printer.print("end")