#---------------------------------------------------------------------------------
# DuckGame host for Nintendo Switch homebrew: mono-nx interpreter + FNA native libs.
# Requires DEVKITPRO, MONO_NX_ROOT (dotnet_runtime of the mono-nx SDK) and
# ICU_NX_INSTALL_DIR. Run build_fna_libs.sh and gen_dlshim.py first (see build.sh).
#---------------------------------------------------------------------------------
.SUFFIXES:

ifeq ($(strip $(DEVKITPRO)),)
$(error "Please set DEVKITPRO in your environment. export DEVKITPRO=<path to>/devkitpro")
endif
ifeq ($(strip $(MONO_NX_ROOT)),)
$(error "Please set MONO_NX_ROOT in your environment.")
endif
ifeq ($(strip $(ICU_NX_INSTALL_DIR)),)
$(error "Please set ICU_NX_INSTALL_DIR in your environment.")
endif

TOPDIR ?= $(CURDIR)
include $(DEVKITPRO)/libnx/switch_rules

APP_TITLE	:=	Duck Game Rebuilt
APP_AUTHOR	:=	DGR contributors
APP_VERSION	:=	0.1.0

TARGET		:=	DuckGame
BUILD		:=	build/host
SOURCES		:=	source mono-nx mono-nx/third_party/ini build/gen
DATA		:=	data
INCLUDES	:=	include mono-nx build/gen
ICON		:=	icon.jpg

FNA_LIBS	:=	$(TOPDIR)/build/fna
MONO_CFG	:=	libnx.arm64.Debug

ARCH	:=	-march=armv8-a+crc+crypto -mtune=cortex-a57 -mtp=soft -fPIE

CFLAGS	:=	-g -Wall -O2 -ffunction-sections $(ARCH) $(DEFINES)
CFLAGS	+=	$(INCLUDE) -D__SWITCH__ \
			-I$(MONO_NX_ROOT)/artifacts/bin/mono/$(MONO_CFG)/include/mono-2.0 \
			-I$(ICU_NX_INSTALL_DIR)/include \
			-DU_DISABLE_RENAMING=1 \
			-DCONFIG_INI_PATH=\"/switch/DuckGame/config.ini\"

CXXFLAGS	:= $(CFLAGS) -fno-rtti -fno-exceptions -std=c++17
ASFLAGS	:=	-g $(ARCH)
LDFLAGS	=	-specs=$(DEVKITPRO)/libnx/switch.specs -g $(ARCH) -Wl,-Map,$(notdir $*.map) \
			-Wl,--wrap=mono_codeman_enable_write_ex -Wl,--wrap=mono_codeman_disable_write_ex \
			-Wl,--wrap=threadCreate

AOT_OBJS	:=	$(wildcard $(TOPDIR)/build/aot/*.o)
ifneq ($(strip $(AOT_OBJS)),)
	CFLAGS	+=	-DDUCKGAME_AOT=1
endif

LIBS	:= \
			$(AOT_OBJS) \
			$(FNA_LIBS)/FNA3D/libFNA3D.a \
			$(FNA_LIBS)/FNA3D/libmojoshader.a \
			$(FNA_LIBS)/FAudio/libFAudio.a \
			$(MONO_NX_ROOT)/artifacts/bin/native/net9.0-libnx-Debug-arm64/libSystem.IO.Compression.Native.a \
			$(MONO_NX_ROOT)/artifacts/bin/native/net9.0-libnx-Debug-arm64/libSystem.Globalization.Native.a \
			$(MONO_NX_ROOT)/artifacts/bin/native/net9.0-libnx-Debug-arm64/libSystem.Native.a \
			$(ICU_NX_INSTALL_DIR)/lib/libicui18n.a \
			$(ICU_NX_INSTALL_DIR)/lib/libicuuc.a \
			$(ICU_NX_INSTALL_DIR)/lib/libicudata.a \
			$(MONO_NX_ROOT)/artifacts/obj/mono/$(MONO_CFG)/out/lib/libmonosgen-2.0.a \
			$(MONO_NX_ROOT)/artifacts/obj/mono/$(MONO_CFG)/out/lib/libmono-component-debugger-static.a \
			$(MONO_NX_ROOT)/artifacts/obj/mono/$(MONO_CFG)/out/lib/libmono-component-diagnostics_tracing-stub-static.a \
			$(MONO_NX_ROOT)/artifacts/obj/mono/$(MONO_CFG)/out/lib/libmono-component-hot_reload-stub-static.a \
			$(MONO_NX_ROOT)/artifacts/obj/mono/$(MONO_CFG)/out/lib/libmono-component-marshal-ilgen-static.a \
			$(MONO_NX_ROOT)/artifacts/obj/mono/$(MONO_CFG)/_deps/fetchzlibng-build/libz.a \
			-pthread \
			`$(PREFIX)pkg-config --libs sdl2` \
			-lEGL -lglapi -ldrm_nouveau \
			-lnx -lm -lstdc++

LIBDIRS	:= $(PORTLIBS) $(LIBNX)

#---------------------------------------------------------------------------------
# Standard devkitPro rules below.
#---------------------------------------------------------------------------------
ifneq ($(notdir $(BUILD)),$(notdir $(CURDIR)))

export OUTPUT	:=	$(CURDIR)/build/$(TARGET)
export TOPDIR	:=	$(CURDIR)

export VPATH	:=	$(foreach dir,$(SOURCES),$(CURDIR)/$(dir)) \
			$(foreach dir,$(DATA),$(CURDIR)/$(dir))

export DEPSDIR	:=	$(CURDIR)/$(BUILD)

CFILES		:=	$(foreach dir,$(SOURCES),$(notdir $(wildcard $(dir)/*.c)))
CPPFILES	:=	$(foreach dir,$(SOURCES),$(notdir $(wildcard $(dir)/*.cpp)))
SFILES		:=	$(foreach dir,$(SOURCES),$(notdir $(wildcard $(dir)/*.s)))
BINFILES	:=	$(foreach dir,$(DATA),$(notdir $(wildcard $(dir)/*.*)))

export LD	:=	$(CXX)

export OFILES_BIN	:=	$(addsuffix .o,$(BINFILES))
export OFILES_SRC	:=	$(CPPFILES:.cpp=.o) $(CFILES:.c=.o) $(SFILES:.s=.o)
export OFILES 	:=	$(OFILES_BIN) $(OFILES_SRC)
export HFILES_BIN	:=	$(addsuffix .h,$(subst .,_,$(BINFILES)))

export INCLUDE	:=	$(foreach dir,$(INCLUDES),-I$(CURDIR)/$(dir)) \
			$(foreach dir,$(LIBDIRS),-I$(dir)/include) \
			-I$(CURDIR)/$(BUILD)

export LIBPATHS	:=	$(foreach dir,$(LIBDIRS),-L$(dir)/lib)

ifneq ($(wildcard $(TOPDIR)/$(ICON)),)
	export NROFLAGS += --icon=$(TOPDIR)/$(ICON)
endif
export NROFLAGS += --nacp=$(OUTPUT).nacp

.PHONY: $(BUILD) clean all

all: $(BUILD)

$(BUILD):
	@[ -d $@ ] || mkdir -p $@
	@$(MAKE) --no-print-directory -C $(BUILD) -f $(CURDIR)/Makefile

clean:
	@rm -fr $(BUILD) build/$(TARGET).nro build/$(TARGET).nacp build/$(TARGET).elf

else
.PHONY:	all

DEPENDS	:=	$(OFILES:.o=.d)

all	:	$(OUTPUT).nro

$(OUTPUT).nro	:	$(OUTPUT).elf $(OUTPUT).nacp

$(OUTPUT).elf	:	$(OFILES)

$(OFILES_SRC)	: $(HFILES_BIN)

%.bin.o	%_bin.h :	%.bin
	@echo $(notdir $<)
	@$(bin2o)

-include $(DEPENDS)

endif
