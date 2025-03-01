local a = {}
local function f (t)
  local info = debug.getinfo(1);
  assert(info.namewhat == "metamethod")
  a.op = info.name
  return info.name
end
setmetatable(a, {
  __index = f; __add = f; __div = f; __mod = f; __concat = f; __pow = f;
  __eq = f; __le = f; __lt = f;
})

local b = setmetatable({}, getmetatable(a))
print(a[3], a^3, a..a)