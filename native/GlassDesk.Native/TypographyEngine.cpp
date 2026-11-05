#define WIN32_LEAN_AND_MEAN
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include "TypographyEngine.h"
#include <winrt/Windows.Data.Text.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <list>
#include <limits>
#include <mutex>
#include <stdexcept>

namespace gd::typography
{
using winrt::com_ptr;
using winrt::check_hresult;
namespace
{
constexpr Token catalog[]={
    {0,"legacy",14,0,400,400,0,Optical::Text,0},
    {1,"clock.capsule",25,32,400,400,0,Optical::Display,1},
    {2,"clock.whiteplate",16,26,400,400,0,Optical::Text,1},
    {3,"clock.legacyAux",10,12,400,400,0,Optical::Small,0},
    {4,"clock.peekHero",34,44,400,400,0,Optical::Display,1},
    {5,"media.capsuleTitle",14,20,500,400,0,Optical::Text,0},
    {6,"media.capsuleArtist",12,16,400,400,0,Optical::Small,0},
    {7,"media.peekTitle",18,26,600,500,0,Optical::Text,0},
    {8,"media.peekArtist",13,18,400,400,0,Optical::Text,0},
    {9,"media.detailsTitle",20,28,600,500,-.01f,Optical::Display,0},
    {10,"media.detailsArtist",14,20,400,400,0,Optical::Text,0},
    {11,"page.title",20,28,600,500,-.01f,Optical::Display,0},
    {12,"section.title",16,24,600,500,0,Optical::Text,0},
    {13,"body",14,20,400,400,0,Optical::Text,0},
    {14,"label",14,20,500,400,0,Optical::Text,0},
    {15,"caption",12,16,400,400,0,Optical::Small,0},
    {16,"caption.settings",13,18,400,400,0,Optical::Text,0},
    {17,"navigation.settings",13,18,400,400,0,Optical::Text,0},
    {18,"navigation.semantic",12,16,500,400,0,Optical::Small,0},
    {19,"widget.clock",36,44,400,400,0,Optical::Display,1},
    {20,"widget.dateValue",48,56,400,400,0,Optical::Display,2},
    {21,"widget.value",36,44,400,400,0,Optical::Display,1},
    {22,"widget.label",12,16,500,400,0,Optical::Small,0}
};
uint32_t Scalar(std::wstring const& text,size_t& index)
{
    uint32_t value=text[index++];
    if(value>=0xd800&&value<=0xdbff&&index<text.size())
    {auto low=uint32_t(text[index]);if(low>=0xdc00&&low<=0xdfff){++index;value=0x10000+((value-0xd800)<<10)+(low-0xdc00);}}
    return value;
}
bool PureLatin(std::wstring const& text)
{
    if(text.empty())return false;
    for(size_t index=0;index<text.size();){auto value=Scalar(text,index);if(value>0x24f&&(value<0x2000||value>0x206f))return false;}
    return true; // Deliberately matches Core FontResolutionPolicy revision 1.
}
bool LegacyClock(std::wstring const& text)
{
    return text.size()==5&&text[2]==L':'&&text[0]>=L'0'&&text[0]<=L'9'&&text[1]>=L'0'&&text[1]<=L'9'
        &&text[3]>=L'0'&&text[3]<=L'9'&&text[4]>=L'0'&&text[4]<=L'9';
}
std::wstring Locale(Request const& request)
{
    // Explicit caller locale wins. Otherwise kana/Hangul provide content context
    // for neighbouring Han; unmarked Han retains the user's locale and fallback.
    if(!request.locale.empty())return request.locale;
    bool kana=false,hangul=false;
    for(size_t index=0;index<request.text.size();){auto value=Scalar(request.text,index);kana|=value>=0x3040&&value<=0x30ff;hangul|=value>=0xac00&&value<=0xd7af;}
    if(kana&&!hangul)return L"ja-JP";
    if(hangul&&!kana)return L"ko-KR";
    wchar_t name[LOCALE_NAME_MAX_LENGTH]{};
    return GetUserDefaultLocaleName(name,LOCALE_NAME_MAX_LENGTH)?name:L"en-US";
}
bool FamilyExists(IDWriteFontCollection* collection,wchar_t const* family)
{UINT32 index{};BOOL exists{};check_hresult(collection->FindFamilyName(family,&index,&exists));return exists!=FALSE;}
std::wstring Name(IDWriteLocalizedStrings* names)
{
    UINT32 index{},length{};BOOL exists{};check_hresult(names->FindLocaleName(L"en-us",&index,&exists));if(!exists)index=0;
    check_hresult(names->GetStringLength(index,&length));std::wstring value(size_t(length)+1,L'\0');check_hresult(names->GetString(index,value.data(),length+1));value.resize(length);return value;
}
struct Key
{
    uint32_t profile{},revision{},role{},weight{},cjkWeight{},flags{};
    float tracking{},size{},lineHeight{},baseline{},width{},height{},dpi{};
    bool bold{},icon{},wrap{},center{},trim{},paragraphCenter{};
    std::wstring family,locale,text;
    bool operator==(Key const&)const=default;
};
struct FormatEntry {Key key;com_ptr<IDWriteTextFormat> value;};
struct LayoutEntry {Key key;std::shared_ptr<Layout const> value;};
// DirectWrite owns script itemization, including inherited combining marks and
// supplementary text. Formatting a script range does not replace shaping or
// fragment the paragraph into separately drawn UTF-16 characters.
class ScriptAnalysis final : public IDWriteTextAnalysisSource, public IDWriteTextAnalysisSink
{
    std::atomic<ULONG> references{1};
    std::wstring const& text;
    std::wstring const& locale;
    IDWriteTextAnalyzer1* analyzer;
public:
    std::vector<ScriptRun> runs;
    ScriptAnalysis(std::wstring const& value,std::wstring const& language,IDWriteTextAnalyzer1* analysis)
        :text(value),locale(language),analyzer(analysis){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** value)override
    {
        if(!value)return E_POINTER;*value=nullptr;
        if(iid==__uuidof(IUnknown)||iid==__uuidof(IDWriteTextAnalysisSource))*value=static_cast<IDWriteTextAnalysisSource*>(this);
        else if(iid==__uuidof(IDWriteTextAnalysisSink))*value=static_cast<IDWriteTextAnalysisSink*>(this);
        else return E_NOINTERFACE;
        AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef()override{return ++references;}
    ULONG STDMETHODCALLTYPE Release()override{auto value=--references;if(!value)delete this;return value;}
    HRESULT STDMETHODCALLTYPE GetTextAtPosition(UINT32 position,wchar_t const** value,UINT32* length)override
    {if(!value||!length)return E_POINTER;*value=position<text.size()?text.data()+position:nullptr;*length=position<text.size()?UINT32(text.size())-position:0;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetTextBeforePosition(UINT32 position,wchar_t const** value,UINT32* length)override
    {if(!value||!length)return E_POINTER;*value=position&&position<=text.size()?text.data():nullptr;*length=position<=text.size()?position:0;return S_OK;}
    DWRITE_READING_DIRECTION STDMETHODCALLTYPE GetParagraphReadingDirection()override{return DWRITE_READING_DIRECTION_LEFT_TO_RIGHT;}
    HRESULT STDMETHODCALLTYPE GetLocaleName(UINT32 position,UINT32* length,wchar_t const** value)override
    {if(!value||!length)return E_POINTER;*length=position<text.size()?UINT32(text.size())-position:0;*value=locale.c_str();return S_OK;}
    HRESULT STDMETHODCALLTYPE GetNumberSubstitution(UINT32 position,UINT32* length,IDWriteNumberSubstitution** value)override
    {if(!value||!length)return E_POINTER;*length=position<text.size()?UINT32(text.size())-position:0;*value=nullptr;return S_OK;}
    HRESULT STDMETHODCALLTYPE SetScriptAnalysis(UINT32 position,UINT32 length,DWRITE_SCRIPT_ANALYSIS const* script)override
    {
        try{if(!script)return E_POINTER;DWRITE_SCRIPT_PROPERTIES properties{};check_hresult(analyzer->GetScriptProperties(*script,&properties));runs.push_back({position,length,properties.isoScriptCode,properties.isoScriptNumber,0,{}});return S_OK;}
        catch(...){return winrt::to_hresult();}
    }
    HRESULT STDMETHODCALLTYPE SetLineBreakpoints(UINT32,UINT32,DWRITE_LINE_BREAKPOINT const*)override{return S_OK;}
    HRESULT STDMETHODCALLTYPE SetBidiLevel(UINT32,UINT32,UINT8,UINT8)override{return S_OK;}
    HRESULT STDMETHODCALLTYPE SetNumberSubstitution(UINT32,UINT32,IDWriteNumberSubstitution*)override{return S_OK;}
};
bool CjkScript(uint32_t isoNumber)
{return isoNumber==500||isoNumber==410||isoNumber==411||isoNumber==412||isoNumber==286||isoNumber==285;}
bool DecimalDigit(uint32_t value)
{
    if(value>=L'0'&&value<=L'9')return true;
    if(value<0x80)return false;
    // The platform's scalar Unicode category avoids an incomplete hand-written
    // Nd table and the supplementary-character limitation of GetStringTypeW.
    return winrt::Windows::Data::Text::UnicodeCharacters::GetGeneralCategory(value)
        ==winrt::Windows::Data::Text::UnicodeGeneralCategory::DecimalDigitNumber;
}
struct SimulatedRun
{UINT32 start{},length{};com_ptr<IDWriteFontFace> face;};
class SimulationCollector final : public IDWriteTextRenderer
{
    std::atomic<ULONG> references{1};
public:
    std::vector<SimulatedRun> runs;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** value)override
    {
        if(!value)return E_POINTER;*value=nullptr;
        if(iid==__uuidof(IUnknown)||iid==__uuidof(IDWriteTextRenderer)||iid==__uuidof(IDWritePixelSnapping)){*value=static_cast<IDWriteTextRenderer*>(this);AddRef();return S_OK;}return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef()override{return ++references;}
    ULONG STDMETHODCALLTYPE Release()override{auto value=--references;if(!value)delete this;return value;}
    HRESULT STDMETHODCALLTYPE IsPixelSnappingDisabled(void*,BOOL* value)override{if(!value)return E_POINTER;*value=TRUE;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetCurrentTransform(void*,DWRITE_MATRIX* value)override{if(!value)return E_POINTER;*value={1,0,0,1,0,0};return S_OK;}
    HRESULT STDMETHODCALLTYPE GetPixelsPerDip(void*,FLOAT* value)override{if(!value)return E_POINTER;*value=1;return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawGlyphRun(void*,FLOAT,FLOAT,DWRITE_MEASURING_MODE,DWRITE_GLYPH_RUN const* glyph,DWRITE_GLYPH_RUN_DESCRIPTION const* description,IUnknown*)override
    {
        try
        {
            if(glyph&&glyph->fontFace&&description&&description->stringLength&&glyph->fontFace->GetSimulations()!=DWRITE_FONT_SIMULATIONS_NONE)
            {com_ptr<IDWriteFontFace> face;face.copy_from(glyph->fontFace);runs.push_back({description->textPosition,description->stringLength,face});}
            return S_OK;
        }catch(...){return winrt::to_hresult();}
    }
    HRESULT STDMETHODCALLTYPE DrawUnderline(void*,FLOAT,FLOAT,DWRITE_UNDERLINE const*,IUnknown*)override{return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawStrikethrough(void*,FLOAT,FLOAT,DWRITE_STRIKETHROUGH const*,IUnknown*)override{return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawInlineObject(void* context,FLOAT x,FLOAT y,IDWriteInlineObject* object,BOOL sideways,BOOL rtl,IUnknown* effect)override
    {return object?object->Draw(context,this,x,y,sideways,rtl,effect):E_INVALIDARG;}
};
}
Token const& TokenForRole(uint32_t role)
{if(role>=std::size(catalog))throw winrt::hresult_invalid_argument();return catalog[role];}
std::vector<Token> Tokens(){return {std::begin(catalog),std::end(catalog)};}

struct Engine::Impl
{
    com_ptr<IDWriteFactory> factory;
    com_ptr<IDWriteFactory2> factory2;
    com_ptr<IDWriteFactory6> factory6;
    com_ptr<IDWriteTextAnalyzer1> analyzer;
    com_ptr<IDWriteFontCollection> system,collection;
    com_ptr<IDWriteFontFallback> fallback;
    Profile profile{Profile::SystemA};uint32_t revision{1};std::wstring privateFamily;
    std::list<FormatEntry> formats;
    std::list<LayoutEntry> layouts;
    CacheStats counters;
    mutable std::mutex mutex;
    explicit Impl(IDWriteFactory* input)
    {
        if(input)factory.copy_from(input);
        else check_hresult(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,__uuidof(IDWriteFactory),reinterpret_cast<IUnknown**>(factory.put())));
        factory2=factory.as<IDWriteFactory2>();factory6=factory.try_as<IDWriteFactory6>();
        com_ptr<IDWriteTextAnalyzer> baseAnalyzer;check_hresult(factory->CreateTextAnalyzer(baseAnalyzer.put()));analyzer=baseAnalyzer.as<IDWriteTextAnalyzer1>();
        check_hresult(factory->GetSystemFontCollection(system.put(),FALSE));
        Configure(Profile::SystemA,1,{});
    }
    void Configure(Profile value,uint32_t tokenRevision,std::wstring const& path)
    {
        if(uint32_t(value)>2||tokenRevision!=1)throw winrt::hresult_invalid_argument();
        // Construct candidate state first, so a failed private-font load cannot
        // corrupt a still-usable engine or leave mislabeled cached layouts.
        auto candidate=system;std::wstring candidateName;
        if(value==Profile::NotoQa)
        {
            if(path.empty()||!factory6)throw winrt::hresult_invalid_argument();
            com_ptr<IDWriteFontFile> file;check_hresult(factory->CreateFontFileReference(path.c_str(),nullptr,file.put()));
            BOOL supported{};DWRITE_FONT_FILE_TYPE type{};DWRITE_FONT_FACE_TYPE face{};UINT32 count{};
            check_hresult(file->Analyze(&supported,&type,&face,&count));if(!supported||!count)throw winrt::hresult_invalid_argument();
            com_ptr<IDWriteFontSetBuilder1> builder;check_hresult(factory6->CreateFontSetBuilder(builder.put()));
            check_hresult(builder->AddFontFile(file.get()));
            com_ptr<IDWriteFontSet> set;check_hresult(builder->CreateFontSet(set.put()));
            com_ptr<IDWriteFontCollection1> privateCollection;
            check_hresult(factory6->CreateFontCollectionFromFontSet(set.get(),privateCollection.put()));
            com_ptr<IDWriteFontFamily> first;check_hresult(privateCollection->GetFontFamily(0,first.put()));
            com_ptr<IDWriteLocalizedStrings> names;check_hresult(first->GetFamilyNames(names.put()));candidateName=Name(names.get());
            candidate=privateCollection.as<IDWriteFontCollection>();
        }
        com_ptr<IDWriteFontFallback> systemFallback;check_hresult(factory2->GetSystemFontFallback(systemFallback.put()));
        com_ptr<IDWriteFontFallback> candidateFallback=systemFallback;
        if(value==Profile::SystemA)
        {
            com_ptr<IDWriteFontFallbackBuilder> builder;check_hresult(factory2->CreateFontFallbackBuilder(builder.put()));
            DWRITE_UNICODE_RANGE ranges[]={{0x2e80,0x9fff},{0xf900,0xfaff},{0x20000,0x323af},{0x1100,0x11ff},{0x3130,0x318f},{0xac00,0xd7af}};
            auto add=[&](wchar_t const* locale,std::initializer_list<wchar_t const*> families)
            {
                std::vector<wchar_t const*> installed;for(auto family:families)if(FamilyExists(system.get(),family))installed.push_back(family);
                if(!installed.empty())check_hresult(builder->AddMapping(ranges,UINT32(std::size(ranges)),installed.data(),UINT32(installed.size()),system.get(),locale,nullptr,1));
            };
            add(L"zh-Hans",{L"Microsoft YaHei UI",L"Microsoft YaHei"});
            add(L"zh-CN",{L"Microsoft YaHei UI",L"Microsoft YaHei"});
            add(L"zh-SG",{L"Microsoft YaHei UI",L"Microsoft YaHei"});
            add(L"zh-Hant",{L"Microsoft JhengHei UI",L"Microsoft JhengHei"});
            add(L"zh-TW",{L"Microsoft JhengHei UI",L"Microsoft JhengHei"});
            add(L"ja",{L"Yu Gothic UI",L"Meiryo UI",L"Meiryo"});
            add(L"ko",{L"Malgun Gothic"});
            check_hresult(builder->AddMappings(systemFallback.get()));check_hresult(builder->CreateFontFallback(candidateFallback.put()));
        }
        collection=candidate;privateFamily=candidateName;fallback=candidateFallback;profile=value;revision=tokenRevision;
        formats.clear();layouts.clear();counters={};
    }
    std::wstring Family(Optical optical,bool icon)const
    {
        if(icon)return L"Segoe Fluent Icons";
        if(profile==Profile::Baseline)return L"Segoe UI Variable Text";
        if(profile==Profile::NotoQa)return privateFamily;
        std::array<wchar_t const*,3> names=optical==Optical::Small
            ?std::array{L"Segoe UI Variable Small",L"Segoe UI Variable Text",L"Segoe UI"}
            :optical==Optical::Display?std::array{L"Segoe UI Variable Display",L"Segoe UI Variable Text",L"Segoe UI"}
            :std::array{L"Segoe UI Variable Text",L"Segoe UI Variable",L"Segoe UI"};
        for(auto name:names)if(FamilyExists(system.get(),name))return name;
        return L"Segoe UI";
    }
    com_ptr<IDWriteTextFormat> Format(Key const& key)
    {
        auto formatKey=key;formatKey.text.clear();
        auto found=std::find_if(formats.begin(),formats.end(),[&](auto const& entry){return entry.key==formatKey;});
        if(found!=formats.end()){++counters.formatHits;auto value=found->value;formats.splice(formats.begin(),formats,found);return value;}
        com_ptr<IDWriteTextFormat> format;
        bool legacy=profile==Profile::Baseline||key.role==0;
        if(!legacy&&!key.icon&&factory6)
        {
            DWRITE_FONT_AXIS_VALUE axes[]={{DWRITE_FONT_AXIS_TAG_WEIGHT,float(key.weight)}};
            com_ptr<IDWriteTextFormat3> advanced;
            check_hresult(factory6->CreateTextFormat(key.family.c_str(),collection.get(),axes,1,key.size,key.locale.c_str(),advanced.put()));
            check_hresult(advanced->SetAutomaticFontAxes(DWRITE_AUTOMATIC_FONT_AXES_OPTICAL_SIZE));format=advanced.as<IDWriteTextFormat>();
        }
        else check_hresult(factory->CreateTextFormat(key.family.c_str(),key.icon?nullptr:legacy?system.get():collection.get(),DWRITE_FONT_WEIGHT(key.weight),DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,key.size,key.locale.c_str(),format.put()));
        check_hresult(format->SetWordWrapping(key.wrap?DWRITE_WORD_WRAPPING_WRAP:DWRITE_WORD_WRAPPING_NO_WRAP));
        check_hresult(format->SetTextAlignment(key.center?DWRITE_TEXT_ALIGNMENT_CENTER:DWRITE_TEXT_ALIGNMENT_LEADING));
        check_hresult(format->SetParagraphAlignment(key.paragraphCenter?DWRITE_PARAGRAPH_ALIGNMENT_CENTER:DWRITE_PARAGRAPH_ALIGNMENT_NEAR));
        if(!legacy&&!key.icon)check_hresult(format.as<IDWriteTextFormat1>()->SetFontFallback(fallback.get()));
        formats.push_front({formatKey,format});while(formats.size()>64)formats.pop_back();return format;
    }
    void AvoidSimulatedFallback(IDWriteTextLayout* layout,std::wstring const& text,std::vector<SimulationFallback>& records)
    {
        // Actual fallback can synthesize bold even when an installed real face
        // exists (run-1 exposed Segoe UI/Emoji fallback). Inspect shaping once
        // per new cached layout, then select a real family member for that run.
        // The baseline path never calls this; no extra draw reaches a surface.
        com_ptr<IDWriteTextLayout> ownedLayout;ownedLayout.copy_from(layout);auto advancedLayout=ownedLayout.try_as<IDWriteTextLayout4>();
        for(int attempt=0;attempt<3;++attempt)
        {
            com_ptr<SimulationCollector> collector;collector.attach(new SimulationCollector());
            check_hresult(layout->Draw(nullptr,collector.get(),0,0));if(collector->runs.empty())return;
            if(attempt==2)throw winrt::hresult_error(E_FAIL,L"Unable to resolve a font fallback without simulations.");
            for(auto const& run:collector->runs)
            {
                if(run.start>=text.size())continue;
                DWRITE_TEXT_RANGE range{run.start,std::min(run.length,UINT32(text.size())-run.start)};
                DWRITE_FONT_WEIGHT requested{};check_hresult(layout->GetFontWeight(run.start,&requested));
                if(advancedLayout)
                {
                    UINT32 count=advancedLayout->GetFontAxisValueCount(run.start);std::vector<DWRITE_FONT_AXIS_VALUE> axes(count);
                    if(count)check_hresult(advancedLayout->GetFontAxisValues(run.start,axes.data(),count));
                    for(auto const& axis:axes)if(axis.axisTag==DWRITE_FONT_AXIS_TAG_WEIGHT)requested=DWRITE_FONT_WEIGHT(std::lround(axis.value));
                }
                std::wstring family;auto face3=run.face.try_as<IDWriteFontFace3>();
                if(face3){com_ptr<IDWriteLocalizedStrings> names;check_hresult(face3->GetFamilyNames(names.put()));family=Name(names.get());}
                uint32_t selectedWeight=400;bool selected=false;
                if(!family.empty())
                {
                    UINT32 index{};BOOL exists{};check_hresult(system->FindFamilyName(family.c_str(),&index,&exists));
                    if(exists)
                    {
                        com_ptr<IDWriteFontFamily> candidates;check_hresult(system->GetFontFamily(index,candidates.put()));int bestScore=std::numeric_limits<int>::max();
                        for(UINT32 i=0;i<candidates->GetFontCount();++i)
                        {
                            com_ptr<IDWriteFont> font;check_hresult(candidates->GetFont(i,font.put()));
                            if(font->GetSimulations()!=DWRITE_FONT_SIMULATIONS_NONE||font->GetStyle()!=DWRITE_FONT_STYLE_NORMAL)continue;
                            int score=std::abs(int(font->GetWeight())-int(requested))+std::abs(int(font->GetStretch())-int(DWRITE_FONT_STRETCH_NORMAL))*1000;
                            if(score<bestScore){bestScore=score;selectedWeight=uint32_t(font->GetWeight());selected=true;}
                        }
                        if(selected){check_hresult(layout->SetFontCollection(system.get(),range));check_hresult(layout->SetFontFamilyName(family.c_str(),range));}
                    }
                }
                if(!selected)family.clear(); // Keep shaping/system fallback at real regular weight.
                check_hresult(layout->SetFontWeight(DWRITE_FONT_WEIGHT(selectedWeight),range));
                if(advancedLayout)
                {DWRITE_FONT_AXIS_VALUE axis{DWRITE_FONT_AXIS_TAG_WEIGHT,float(selectedWeight)};check_hresult(advancedLayout->SetFontAxisValues(&axis,1,range));}
                records.push_back({range.startPosition,range.length,uint32_t(requested),selectedWeight,family});
            }
        }
    }
    std::shared_ptr<Layout const> Create(Request const& input)
    {
        if(!std::isfinite(input.fontSize)||!std::isfinite(input.width)||!std::isfinite(input.height)||!std::isfinite(input.dpi)
            ||!std::isfinite(input.lineHeight)||!std::isfinite(input.baseline)||!std::isfinite(input.trackingEm)
            ||input.fontSize<=0||input.fontSize>512||input.width<=0||input.width>32768||input.height<=0||input.height>32768
            ||input.dpi<48||input.dpi>768||input.lineHeight<0||input.baseline<0||input.text.size()>65536||input.fontWeight>999||input.cjkWeight>999||(input.flags&~0x303u))throw winrt::hresult_invalid_argument();
        uint32_t clockFace=input.flags&0x300u;
        if(clockFace&&input.role!=1&&input.role!=2&&input.role!=4)throw winrt::hresult_invalid_argument();
        auto const& token=TokenForRole(input.role);auto legacy=profile==Profile::Baseline||input.role==0;
        Optical optical=token.optical;
        // Explicit test sizes and Legacy role follow the optical size thresholds.
        if(input.role==0||input.fontSize!=token.size)optical=input.fontSize<=12?Optical::Small:input.fontSize>=20?Optical::Display:Optical::Text;
        uint32_t weight=input.fontWeight?input.fontWeight:(input.bold?600:token.latinWeight);
        uint32_t cjk=input.cjkWeight?input.cjkWeight:(weight==token.latinWeight?token.cjkWeight:(weight>=600?500:400));
        if(legacy){weight=(input.bold||weight>=600)?600:400;cjk=weight;}
        auto text=input.text;if(!input.wrap)for(auto& character:text)if(character==L'\r'||character==L'\n'||character==L'\t')character=L' ';
        auto locale=legacy?std::wstring{}:Locale(input);
        uint32_t flags=legacy?(LegacyClock(text)?TabularNumbers:0):input.flags;
        float tracking=legacy||flags||!PureLatin(text)?0:input.trackingEm;
        auto family=legacy&&!input.icon?std::wstring(L"Segoe UI Variable Text"):Family(optical,input.icon);
        if(!legacy&&clockFace)
        {
            auto candidate=clockFace==0x100u?L"Cambria":clockFace==0x200u?L"Bahnschrift":L"Consolas";
            if(FamilyExists(system.get(),candidate))family=candidate;
        }
        Key key{uint32_t(profile),revision,input.role,weight,cjk,flags,tracking,input.fontSize,legacy?0:input.lineHeight,legacy?0:input.baseline,input.width,input.height,input.dpi,input.bold,input.icon,input.wrap,input.center,input.trim,input.paragraphCenter,family,locale,text};
        auto found=std::find_if(layouts.begin(),layouts.end(),[&](auto const& entry){return entry.key==key;});
        if(found!=layouts.end()){++counters.layoutHits;auto value=found->value;layouts.splice(layouts.begin(),layouts,found);return value;}
        auto format=Format(key);auto result=std::make_shared<Layout>();
        check_hresult(factory->CreateTextLayout(text.c_str(),UINT32(text.size()),format.get(),key.width,key.height,result->textLayout.put()));
        auto layout=result->textLayout;
        if(!legacy&&!input.icon)
        {
            if(auto advanced=layout.try_as<IDWriteTextLayout4>())
                check_hresult(advanced->SetAutomaticFontAxes(DWRITE_AUTOMATIC_FONT_AXES_OPTICAL_SIZE));
            com_ptr<ScriptAnalysis> analysis;analysis.attach(new ScriptAnalysis(text,locale,analyzer.get()));
            if(!text.empty())check_hresult(analyzer->AnalyzeScript(analysis.get(),0,UINT32(text.size()),analysis.get()));
            for(auto& script:analysis->runs)
            {
                bool cjkRun=CjkScript(script.isoScriptNumber);script.requestedWeight=cjkRun?cjk:weight;script.locale=locale;
                if(script.isoScriptNumber==410||script.isoScriptNumber==411||script.isoScriptNumber==412)script.locale=L"ja-JP";
                else if(script.isoScriptNumber==286)script.locale=L"ko-KR";
                else if(script.isoScriptNumber==285)script.locale=L"zh-TW";
                DWRITE_TEXT_RANGE range{script.start,script.length};
                check_hresult(layout->SetLocaleName(script.locale.c_str(),range));
                if(cjkRun)
                {
                    check_hresult(layout->SetFontWeight(DWRITE_FONT_WEIGHT(cjk),range));
                    if(auto advanced=layout.try_as<IDWriteTextLayout4>())
                    {DWRITE_FONT_AXIS_VALUE axis{DWRITE_FONT_AXIS_TAG_WEIGHT,float(cjk)};check_hresult(advanced->SetFontAxisValues(&axis,1,range));}
                }
            }
            result->scripts=std::move(analysis->runs);
            if(tracking!=0)check_hresult(layout.as<IDWriteTextLayout1>()->SetCharacterSpacing(0,tracking*key.size,0,{0,UINT32(text.size())}));
        }
        if(flags&3u)
        {
            com_ptr<IDWriteTypography> typography;check_hresult(factory->CreateTypography(typography.put()));
            check_hresult(typography->AddFontFeature({DWRITE_FONT_FEATURE_TAG_TABULAR_FIGURES,1}));
            if(flags&TabularNumbers)check_hresult(layout->SetTypography(typography.get(),{0,UINT32(text.size())}));
            else
            {
                for(size_t begin=0;begin<text.size();)
                {
                    auto end=begin;auto scalar=Scalar(text,end);
                    if(!DecimalDigit(scalar)){begin=end;continue;}
                    while(end<text.size()){auto next=end;if(!DecimalDigit(Scalar(text,next)))break;end=next;}
                    check_hresult(layout->SetTypography(typography.get(),{UINT32(begin),UINT32(end-begin)}));begin=end;
                }
            }
        }
        if(!legacy&&(key.lineHeight>0||key.baseline>0))
        {
            UINT32 count{};layout->GetLineMetrics(nullptr,0,&count);std::vector<DWRITE_LINE_METRICS> natural(count);
            if(count)check_hresult(layout->GetLineMetrics(natural.data(),count,&count));
            float height=key.lineHeight>0?std::min(key.lineHeight,key.height):count?natural.front().height:key.height;
            float baseline=key.baseline>0?key.baseline:count?natural.front().baseline+(height-natural.front().height)/2:height*.8f;
            check_hresult(layout->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM,height,std::clamp(baseline,0.0f,height)));
        }
        if(input.trim&&!input.icon)
        {com_ptr<IDWriteInlineObject> ellipsis;check_hresult(factory->CreateEllipsisTrimmingSign(format.get(),ellipsis.put()));DWRITE_TRIMMING trimming{DWRITE_TRIMMING_GRANULARITY_CHARACTER,0,0};check_hresult(layout->SetTrimming(&trimming,ellipsis.get()));}
        if(!legacy&&!input.icon)AvoidSimulatedFallback(layout.get(),text,result->simulationFallbacks);
        DWRITE_TEXT_METRICS metrics{};check_hresult(layout->GetMetrics(&metrics));UINT32 count{};layout->GetLineMetrics(nullptr,0,&count);std::vector<DWRITE_LINE_METRICS> lines(count);
        if(count)check_hresult(layout->GetLineMetrics(lines.data(),count,&count));
        result->metrics={metrics.width,metrics.widthIncludingTrailingWhitespace,metrics.height,metrics.top+(count?lines.front().baseline:0),count?lines.front().height:0,metrics.lineCount};
        result->text=text;result->requestedFamily=family;result->resolvedLocale=locale;result->profile=profile;result->revision=revision;
        result->latinWeight=weight;result->cjkWeight=cjk;result->flags=flags;result->trackingEm=tracking;result->optical=optical;
        result->opticalAxisRequested=!legacy&&!input.icon&&bool(factory6);
        layouts.push_front({key,result});++counters.layoutsCreated;while(layouts.size()>128)layouts.pop_back();return result;
    }
};
Engine::Engine(IDWriteFactory* factory):impl(std::make_unique<Impl>(factory)){}
Engine::~Engine()=default;
void Engine::Configure(Profile profile,uint32_t revision,std::wstring const& path){std::lock_guard lock(impl->mutex);impl->Configure(profile,revision,path);}
std::shared_ptr<Layout const> Engine::CreateLayout(Request const& request){std::lock_guard lock(impl->mutex);return impl->Create(request);}
CacheStats Engine::Stats()const{std::lock_guard lock(impl->mutex);auto value=impl->counters;value.formats=impl->formats.size();value.layouts=impl->layouts.size();return value;}
IDWriteFactory* Engine::Factory()const{return impl->factory.get();}
std::wstring Engine::PrivateFamily()const{std::lock_guard lock(impl->mutex);return impl->privateFamily;}
InkBounds InkBoundsForLayout(IDWriteTextLayout* layout)
{
    if(!layout)throw winrt::hresult_invalid_argument();
    DWRITE_OVERHANG_METRICS overhang{};
    check_hresult(layout->GetOverhangMetrics(&overhang));
    FLOAT const layoutWidth=layout->GetMaxWidth();
    FLOAT const layoutHeight=layout->GetMaxHeight();
    return {-overhang.left,-overhang.top,layoutWidth+overhang.right,layoutHeight+overhang.bottom};
}
}
