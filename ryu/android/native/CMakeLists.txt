cmake_minimum_required(VERSION 3.22.1)
project(ryujinx_android LANGUAGES C CXX)

add_library(ryujinx_android SHARED native_window_bridge.cpp)

find_library(android-lib android REQUIRED)
find_library(log-lib log REQUIRED)

target_compile_features(ryujinx_android PRIVATE cxx_std_17)
target_compile_options(ryujinx_android PRIVATE -Wall -Wextra -Werror)
target_link_libraries(ryujinx_android PRIVATE ${android-lib} ${log-lib})
